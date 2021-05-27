using System;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using App1;
using Microsoft.MixedReality.WebRTC;

namespace CollaborativeStreamingApp
{
    public sealed partial class MainPage : Page
    {
        public readonly PixelBitsDistribution pixelBitsDistribution = new PixelBitsDistribution(8, 2, 2);

        private PeerConnection _peerConnection;
        private NodeDssSignaler _signaler;
        private object _remoteVideoLock = new object();
        private bool _remoteVideoPlaying = false;
        private MediaStreamSource _remoteVideoSource;
        private VideoBridge _remoteVideoBridge = new VideoBridge(5);
        private RemoteVideoTrack _remoteVideoTrack;

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Application.Current.Suspending += App_Suspending;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // New peer connection
            _peerConnection = new PeerConnection();
            // Use STUN to work behind NAT
            var config = new PeerConnectionConfiguration
            {
                IceServers = new List<IceServer> {
                    new IceServer{ Urls = { "stun:stun.l.google.com:19302" } }
                }
            };
            await _peerConnection.InitializeAsync(config);
            Debugger.Log(0, "", "<Server> | Peer connection initialized successfully.\n");
            _peerConnection.LocalSdpReadytoSend += Peer_LocalSdpReadyToSend;
            _peerConnection.IceCandidateReadytoSend += Peer_IceCandidateReadyToSend;

            // Initialize the signaler
            _signaler = new NodeDssSignaler()
            {
                HttpServerAddress = "http://127.0.0.1:3000/",
                LocalPeerId = "server",
                RemotePeerId = "client",
            };
            _signaler.OnMessage += async (NodeDssSignaler.Message msg) =>
            {
                switch (msg.MessageType)
                {
                    case NodeDssSignaler.Message.WireMessageType.Offer:
                        // Wait for the offer to be applied
                        await _peerConnection.SetRemoteDescriptionAsync(msg.ToSdpMessage());
                        // Once applied, create an answer
                        _peerConnection.CreateAnswer();
                        break;

                    case NodeDssSignaler.Message.WireMessageType.Answer:
                        // No need to await this call; we have nothing to do after it
                        await _peerConnection.SetRemoteDescriptionAsync(msg.ToSdpMessage());
                        break;

                    case NodeDssSignaler.Message.WireMessageType.Ice:
                        _peerConnection.AddIceCandidate(msg.ToIceCandidate());
                        break;
                }
            };
            _signaler.StartPollingAsync();

            _peerConnection.Connected += () => {
                Debugger.Log(0, "", "<Server> | PeerConnection: connected.\n");
            };
            _peerConnection.IceStateChanged += (IceConnectionState newState) => {
                Debugger.Log(0, "", $"<Server> | ICE state: {newState}\n");
            };

            _peerConnection.VideoTrackAdded += (RemoteVideoTrack track) => {
                Debugger.Log(0, "", $"<Server> | Video track added: {track.Name}\n");
                _remoteVideoTrack = track;
                _remoteVideoTrack.I420AVideoFrameReady += RemoteVideo_I420AFrameReady;
            };
        }

        private void App_Suspending(object sender, SuspendingEventArgs e)
        {
            if (_peerConnection != null)
            {
                _peerConnection.Close();
                _peerConnection.Dispose();
                _peerConnection = null;
            }
            if (_signaler != null)
            {
                _signaler.StopPollingAsync();
                _signaler = null;
            }
            remoteVideoPlayerElement.SetMediaPlayer(null);
        }

        private void RunOnMainThread(Windows.UI.Core.DispatchedHandler handler)
        {
            if (Dispatcher.HasThreadAccess)
            {
                handler.Invoke();
            }
            else
            {
                // Note: use a discard "_" to silence CS4014 warning
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, handler);
            }
        }

        private void RemoteVideo_I420AFrameReady(I420AVideoFrame frame)
        {
            //var resizedFrame = Scaler.GetResizeFrame(frame, 1920, 1080, pixelBitsDistribution);
            //frame = resizedFrame;
            lock (_remoteVideoLock)
            {
                if (!_remoteVideoPlaying)
                {
                    _remoteVideoPlaying = true;
                    uint width = frame.width;
                    uint height = frame.height;
                    RunOnMainThread(() =>
                    {
                        // Bridge the remote video track with the remote media player UI
                        int framerate = 30; // assumed, for lack of an actual value
                        _remoteVideoSource = CreateI420VideoStreamSource(width, height,
                            framerate);
                        var remoteVideoPlayer = new MediaPlayer();
                        remoteVideoPlayer.Source = MediaSource.CreateFromMediaStreamSource(_remoteVideoSource);
                        remoteVideoPlayerElement.SetMediaPlayer(remoteVideoPlayer);
                        remoteVideoPlayer.Play();
                    });
                }
            }
            _remoteVideoBridge.HandleIncomingVideoFrame(frame);
        }
        private MediaStreamSource CreateI420VideoStreamSource(uint width, uint height, int framerate)
        {
            if (width == 0)
            {
                throw new ArgumentException("Invalid zero width for video.", "width");
            }
            if (height == 0)
            {
                throw new ArgumentException("Invalid zero height for video.", "height");
            }
            // Note: IYUV and I420 have same memory layout (though different FOURCC)
            // https://docs.microsoft.com/en-us/windows/desktop/medfound/video-subtype-guids
            var videoProperties = VideoEncodingProperties.CreateUncompressed(
                MediaEncodingSubtypes.Iyuv, width, height);
            var videoStreamDesc = new VideoStreamDescriptor(videoProperties);
            videoStreamDesc.EncodingProperties.FrameRate.Numerator = (uint)framerate;
            videoStreamDesc.EncodingProperties.FrameRate.Denominator = 1;
            // Bitrate in bits per second : framerate * frame pixel size * I420=12bpp
            videoStreamDesc.EncodingProperties.Bitrate = ((uint)framerate * width * height * 12);
            var videoStreamSource = new MediaStreamSource(videoStreamDesc);
            videoStreamSource.BufferTime = TimeSpan.Zero;
            videoStreamSource.SampleRequested += OnMediaStreamSourceRequested;
            videoStreamSource.IsLive = true; // Enables optimizations for live sources
            videoStreamSource.CanSeek = false; // Cannot seek live WebRTC video stream
            return videoStreamSource;
        }

        private void Peer_LocalSdpReadyToSend(SdpMessage message)
        {
            var msg = NodeDssSignaler.Message.FromSdpMessage(message);
            _signaler.SendMessageAsync(msg);
        }

        private void Peer_IceCandidateReadyToSend(IceCandidate iceCandidate)
        {
            var msg = NodeDssSignaler.Message.FromIceCandidate(iceCandidate);
            _signaler.SendMessageAsync(msg);
        }

        private void OnMediaStreamSourceRequested(MediaStreamSource sender,
            MediaStreamSourceSampleRequestedEventArgs args)
        {
            VideoBridge videoBridge;
            if (sender == _remoteVideoSource)
                videoBridge = _remoteVideoBridge;
            else
                return;
            videoBridge.TryServeVideoFrame(args);
        }

        private void Log(object obj)
        {
            Debugger.Log(0, "", obj.ToString() + "\n");
        }
    }
}

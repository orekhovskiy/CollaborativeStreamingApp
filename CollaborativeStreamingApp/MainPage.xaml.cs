using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using Windows.ApplicationModel;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using App1;
using Microsoft.MixedReality.WebRTC;
using System.Collections;

namespace CollaborativeStreamingApp
{
    public sealed partial class MainPage : Page
    {
        public readonly struct PixelBitsDistribution
        {
            public PixelBitsDistribution(int yBits, int uBits, int vBits)
            {
                YBits = yBits;
                UBits = uBits;
                VBits = vBits;
            }
            public readonly int YBits;
            public readonly int UBits;
            public readonly int VBits;
        };
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
            this.InitializeComponent();
            this.Loaded += OnLoaded;
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
            lock (_remoteVideoLock)
            {
                ResizeFrame(frame, 640, 360);

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

        private void Log(String msg)
        {
            Debugger.Log(0, "", msg + "\n");
        }

        private void ResizeFrame(I420AVideoFrame frame, int desiredWidth, int desiredHeight)
        {

            int pixelSize = (int)frame.width * (int)frame.height;
            int byteSize = (pixelSize / 2 * 3); // I420 = 12 bits per pixe
            byte[] frameBytes = new byte[byteSize];
            frame.CopyTo(frameBytes);

            var dataYBits = TakeTotalUnitBits(frameBytes, 12, 0, 8);
            var dataUBits = TakeTotalUnitBits(frameBytes, 12, 8, 2);
            var dataVBits = TakeTotalUnitBits(frameBytes, 12, 10, 2);

            var dataYBitsResized = ResizePixels(dataYBits, frame.width, frame.height, desiredWidth, desiredHeight, pixelBitsDistribution.YBits);
            var dataUBitsResized = ResizePixels(dataUBits, frame.width, frame.height, desiredWidth, desiredHeight, pixelBitsDistribution.UBits);
            var dataVBitsResized = ResizePixels(dataVBits, frame.width, frame.height, desiredWidth, desiredHeight, pixelBitsDistribution.VBits);

            var dataYBytesResized = ConvertToByteArray(dataYBitsResized);
            var dataUBytesResized = ConvertToByteArray(dataUBitsResized);
            var dataVBytesResized = ConvertToByteArray(dataVBitsResized);

            frame.height = (uint)desiredHeight;
            frame.width = (uint)desiredWidth;
            frame.strideY = desiredWidth;
            frame.strideU = desiredHeight * 8 / 9 ;
            frame.strideV = desiredHeight;
            Marshal.Copy(dataYBytesResized, 0, frame.dataY, dataYBytesResized.Length);
            Marshal.Copy(dataUBytesResized, 0, frame.dataU, dataUBytesResized.Length);
            Marshal.Copy(dataVBytesResized, 0, frame.dataY, dataVBytesResized.Length);
        }

        private BitArray TakeTotalUnitBits(byte[] bytes, int totalBitsPerPixel, int bitsOffset,  int unitBits)
        {
            var bits = new BitArray(bytes);
            var bitsToSkip = bits.Length / totalBitsPerPixel * bitsOffset;
            var bitsToTake = bits.Length / totalBitsPerPixel * unitBits;
            var totalUnitBits = SliceBitArray(bits, bitsToSkip, bitsToTake);
            return totalUnitBits;
        }

        public BitArray ResizePixels(BitArray bits, uint w1, uint h1, int w2, int h2, int unitSize)
        {
            BitArray temp = new BitArray(w2 * h2);
            double x_ratio = w1 / (double)w2;
            double y_ratio = h1 / (double)h2;
            double px, py;
            for (int i = 0; i < h2; i+= unitSize)
            {
                for (int j = 0; j < w2; j+= unitSize)
                {
                    px = Math.Floor(j * x_ratio);
                    py = Math.Floor(i * y_ratio);
                    for (int k = 0; k < unitSize; k++)
                    {
                        temp[(i * w2) + j + k] = bits[(int)((py * w1) + px) + k];
                    }
                }
            }
            return temp;
        }
        private BitArray SliceBitArray(BitArray array, int skip, int take)
        {
            var temp = new bool[take];
            for (var i = 0; i < take; i++)
            {
                temp[i] = array[skip + i];
            }
            return new BitArray(temp);
        }

        private byte[] ConvertToByteArray(BitArray bits)
        {
            if (bits.Length % 8 != 0) throw new Exception("Unable to cast");
            byte[] bytes = new byte[bits.Length / 8];
            bits.CopyTo(bytes, 0);
            return bytes;
        }
    }
}

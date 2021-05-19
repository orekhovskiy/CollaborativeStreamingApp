using Microsoft.MixedReality.WebRTC;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace Client
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {

        private PeerConnection _peerConnection;
        DeviceAudioTrackSource _microphoneSource;
        DeviceVideoTrackSource _webcamSource;
        LocalAudioTrack _localAudioTrack;
        LocalVideoTrack _localVideoTrack;
        Transceiver _audioTransceiver;
        Transceiver _videoTransceiver;
        private MediaStreamSource _localVideoSource;
        private VideoBridge _localVideoBridge = new VideoBridge(3);
        private bool _localVideoPlaying = false;
        private object _localVideoLock = new object();
        private NodeDssSignaler _signaler;

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoadedAsync;
            Application.Current.Suspending += App_Suspending;
        }

        private void App_Suspending(object sender, SuspendingEventArgs e)
        {
            if (_peerConnection != null)
            {
                _peerConnection.Close();
                _peerConnection.Dispose();
                _peerConnection = null;
            }
            localVideoPlayerElement.SetMediaPlayer(null);
            if (_signaler != null)
            {
                _signaler.StopPollingAsync();
                _signaler = null;
            }
        }

        private async void OnLoadedAsync(object sender, RoutedEventArgs e)
        {
            // Request access to microphone and camera
            MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
            settings.StreamingCaptureMode = StreamingCaptureMode.AudioAndVideo;
            var capture = new MediaCapture();
            await capture.InitializeAsync(settings);

            // Retrieve a list of available video capture devices (webcams).
            IReadOnlyList<VideoCaptureDevice> deviceList =
                await DeviceVideoTrackSource.GetCaptureDevicesAsync();
            System.Diagnostics.Debugger.Log(0, "", $"<Client> | Web cam's number is: {deviceList.Count}\n");
            foreach (var device in deviceList)
            {
                Debugger.Log(0, "", $"<Client> | Web cam {device.name} (id: {device.id})\n");
            }
            inputDevice.ItemsSource = deviceList.Select(o => o.name);
        }

        private async Task<VideoCaptureDevice> GetVideoCaptureDevice(String deviceName)
        {
            var devices = await DeviceVideoTrackSource.GetCaptureDevicesAsync();
            return devices.Where(o => o.name == deviceName).SingleOrDefault();
        }

        private async void CreateOffer(object sender, RoutedEventArgs e)
        {
            var deviceName = (String)inputDevice.SelectedValue;
            if (deviceName == null)
            {
                return;
            }
            var device = await GetVideoCaptureDevice(deviceName);
            if (device.Equals(default)) return;
            _peerConnection = new PeerConnection();
            var config = new PeerConnectionConfiguration
            {
                IceServers = new List<IceServer> {
                    new IceServer{ Urls = { "stun:stun.l.google.com:19302" } }
                }
            };
            await _peerConnection.InitializeAsync(config);
            Debugger.Log(0, "", "<Client> | Peer connection initialized successfully.\n");
            LocalVideoDeviceInitConfig videoConfig;
            switch(device.name)
            {
                case "USB2.0 VGA UVC WebCam":
                    return;
                    videoConfig = new LocalVideoDeviceInitConfig()
                    {
                        videoDevice = device,
                        framerate = 30,
                        width = 640,
                        height = 480
                    };
                    break;
                default:
                    videoConfig = new LocalVideoDeviceInitConfig()
                    {
                        videoDevice = device,
                        framerate = 30,
                        width = 1920,
                        height = 1080
                    };
                    break;
            }

            _webcamSource = await DeviceVideoTrackSource.CreateAsync(videoConfig);
            var videoTrackConfig = new LocalVideoTrackInitConfig
            {
                trackName = "webcam_track"
            };
            _localVideoTrack = LocalVideoTrack.CreateFromSource(_webcamSource, videoTrackConfig);
            _microphoneSource = await DeviceAudioTrackSource.CreateAsync();
            var audioTrackConfig = new LocalAudioTrackInitConfig
            {
                trackName = "microphone_track"
            };
            _localAudioTrack = LocalAudioTrack.CreateFromSource(_microphoneSource, audioTrackConfig);

            _audioTransceiver = _peerConnection.AddTransceiver(MediaKind.Audio);
            _videoTransceiver = _peerConnection.AddTransceiver(MediaKind.Video);
            _audioTransceiver.LocalAudioTrack = _localAudioTrack;
            _videoTransceiver.LocalVideoTrack = _localVideoTrack;

            _webcamSource = await DeviceVideoTrackSource.CreateAsync();
            _webcamSource.I420AVideoFrameReady += LocalI420AFrameReady;
            _peerConnection.LocalSdpReadytoSend += Peer_LocalSdpReadytoSend;
            _peerConnection.IceCandidateReadytoSend += Peer_IceCandidateReadytoSend;

            // Initialize the signaler
            _signaler = new NodeDssSignaler()
            {
                HttpServerAddress = "http://127.0.0.1:3000/",
                LocalPeerId = "client",
                RemotePeerId = "server",
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
                        Debugger.Log(0, "", $"<Client> | On answer\n");
                        SdpMessage sdpMessage = msg.ToSdpMessage();
                        await _peerConnection.SetRemoteDescriptionAsync(sdpMessage);
                        break;

                    case NodeDssSignaler.Message.WireMessageType.Ice:
                        _peerConnection.AddIceCandidate(msg.ToIceCandidate());
                        break;
                }
            };
            _signaler.StartPollingAsync();
            _peerConnection.Connected += () => {
                Debugger.Log(0, "", "<Client> | PeerConnection: connected.\n");
            };
            _peerConnection.IceStateChanged += (IceConnectionState newState) => {
                Debugger.Log(0, "", $"<Client> | ICE state: {newState}\n");
            };
            _peerConnection.CreateOffer();
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

        private void OnMediaStreamSourceRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            VideoBridge videoBridge;
            if (sender == _localVideoSource)
                videoBridge = _localVideoBridge;
            else
                return;
            videoBridge.TryServeVideoFrame(args);
        }

        private void LocalI420AFrameReady(I420AVideoFrame frame)
        {
            lock (_localVideoLock)
            {
                if (!_localVideoPlaying)
                {
                    _localVideoPlaying = true;

                    // Capture the resolution into local variable useable from the lambda below
                    uint width = frame.width;
                    uint height = frame.height;

                    // Defer UI-related work to the main UI thread
                    RunOnMainThread(() =>
                    {
                        // Bridge the local video track with the local media player UI
                        int framerate = 30; // assumed, for lack of an actual value
                        _localVideoSource = CreateI420VideoStreamSource(
                            width, height, framerate);
                        var localVideoPlayer = new MediaPlayer();
                        localVideoPlayer.Source = MediaSource.CreateFromMediaStreamSource(
                            _localVideoSource);
                        localVideoPlayerElement.SetMediaPlayer(localVideoPlayer);
                        localVideoPlayer.Play();
                    });
                }
            }
            // Enqueue the incoming frame into the video bridge; the media player will
            // later dequeue it as soon as it's ready.
            _localVideoBridge.HandleIncomingVideoFrame(frame);
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
        private void Peer_LocalSdpReadytoSend(SdpMessage message)
        {
            var msg = NodeDssSignaler.Message.FromSdpMessage(message);
            _signaler.SendMessageAsync(msg);
        }

        private void Peer_IceCandidateReadytoSend(IceCandidate iceCandidate)
        {
            var msg = NodeDssSignaler.Message.FromIceCandidate(iceCandidate);
            _signaler.SendMessageAsync(msg);
        }
    }
}

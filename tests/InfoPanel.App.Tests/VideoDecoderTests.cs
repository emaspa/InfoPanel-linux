using System;
using InfoPanel.Video;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class FfmpegArgumentTests
    {
        private static string Join(bool isLive, (int, int)? scale, bool hwaccel, string source = "/videos/a b.mp4") =>
            string.Join(' ', FfmpegVideoDecoder.BuildArguments(source, isLive, scale, hwaccel));

        [Fact]
        public void File_NoScale_NoHwaccel()
        {
            Assert.Equal(
                "-hide_banner -loglevel error -nostdin -stream_loop -1 -threads 4 -i /videos/a b.mp4 -an -sn -filter_threads 1 -f rawvideo -pix_fmt rgba pipe:1",
                Join(isLive: false, scale: null, hwaccel: false));
        }

        [Fact]
        public void File_Scale_Hwaccel()
        {
            var args = FfmpegVideoDecoder.BuildArguments("/v.mp4", isLive: false, (1600, 720), hwaccel: true);

            Assert.Equal(
                "-hide_banner -loglevel error -nostdin -hwaccel auto -stream_loop -1 -threads 4 -i /v.mp4 -an -sn -filter_threads 1 -vf scale=1600:720:flags=bilinear -f rawvideo -pix_fmt rgba pipe:1",
                string.Join(' ', args));
            // -hwaccel is an input option: it must come before -i.
            Assert.True(args.IndexOf("-hwaccel") < args.IndexOf("-i"));
        }

        [Fact]
        public void File_OddScale_IsPassedThrough()
        {
            Assert.Contains("-vf scale=801:451:flags=bilinear", Join(isLive: false, scale: (801, 451), hwaccel: false));
        }

        [Fact]
        public void SourceWithSpacesAndQuotes_IsOneArgument()
        {
            var args = FfmpegVideoDecoder.BuildArguments("/v/it's \"x\".mp4", isLive: false, null, hwaccel: false);
            Assert.Equal("/v/it's \"x\".mp4", args[args.IndexOf("-i") + 1]);
        }

        [Fact]
        public void Live_UsesTcp_NoLoop_IgnoresScale()
        {
            var joined = Join(isLive: true, scale: (640, 360), hwaccel: false, source: "rtsp://cam/stream");

            Assert.Equal(
                "-hide_banner -loglevel error -nostdin -rtsp_transport tcp -threads 4 -i rtsp://cam/stream -an -sn -filter_threads 1 -f rawvideo -pix_fmt rgba pipe:1",
                joined);
        }

        [Fact]
        public void Live_Hwaccel()
        {
            Assert.StartsWith(
                "-hide_banner -loglevel error -nostdin -hwaccel auto -rtsp_transport tcp -threads 4 -i rtsp://cam",
                Join(isLive: true, scale: null, hwaccel: true, source: "rtsp://cam"));
        }

        [Theory]
        [InlineData("rtsp://cam", true)]
        [InlineData("RTSPS://cam", true)]
        [InlineData("https://host/v.mp4", false)]
        [InlineData("/home/u/v.mp4", false)]
        public void IsLiveSource(string source, bool expected)
        {
            Assert.Equal(expected, FfmpegVideoDecoder.IsLiveSource(source));
        }
    }

    public class VideoOutputSizePolicyTests
    {
        private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

        [Fact]
        public void NoOutputBeforeFirstRequest()
        {
            Assert.Null(new VideoOutputSizePolicy(2400, 1080).Output);
        }

        [Fact]
        public void FirstRequest_SetsOutputImmediately()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);

            Assert.True(policy.Request(1600, 720, Ms(0)));
            Assert.Equal((1600, 720), policy.Output);
        }

        [Fact]
        public void FirstRequest_ClampedToSource()
        {
            var policy = new VideoOutputSizePolicy(1920, 1080);

            Assert.True(policy.Request(2560, 720, Ms(0)));
            Assert.Equal((1920, 720), policy.Output);
        }

        [Fact]
        public void OddSizes_AreKept()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);

            policy.Request(801, 451, Ms(0));
            Assert.Equal((801, 451), policy.Output);
        }

        [Fact]
        public void SameOrSmallerRequests_NeverChangeOutput()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(1600, 720, Ms(0));

            for (var t = 0; t <= 5000; t += 100)
            {
                Assert.False(policy.Request(400, 180, Ms(t)));
                Assert.False(policy.Request(1600, 720, Ms(t)));
            }

            Assert.Equal((1600, 720), policy.Output);
        }

        [Fact]
        public void LargerRequest_GrowsOnlyAfterOneSecond()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(400, 180, Ms(0));

            Assert.False(policy.Request(1600, 720, Ms(100)));
            Assert.False(policy.Request(1600, 720, Ms(600)));
            Assert.False(policy.Request(1600, 720, Ms(1099)));
            Assert.Equal((400, 180), policy.Output);

            Assert.True(policy.Request(1600, 720, Ms(1100)));
            Assert.Equal((1600, 720), policy.Output);

            // Settled: the same request no longer restarts.
            Assert.False(policy.Request(1600, 720, Ms(3000)));
        }

        [Fact]
        public void Grow_WithInterleavedSmallConsumer_StillHappens()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(400, 180, Ms(0));

            var grew = false;
            for (var t = 40; t <= 1200 && !grew; t += 40)
            {
                Assert.False(policy.Request(400, 180, Ms(t)));
                grew = policy.Request(1600, 720, Ms(t));
            }

            Assert.True(grew);
            Assert.Equal((1600, 720), policy.Output);
        }

        [Fact]
        public void Grow_IsCappedAtSource()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(1600, 720, Ms(0));

            policy.Request(4000, 2000, Ms(100));
            Assert.True(policy.Request(4000, 2000, Ms(1100)));
            Assert.Equal((2400, 1080), policy.Output);

            Assert.False(policy.Request(4000, 2000, Ms(5000)));
        }

        [Fact]
        public void Grow_TakesMaxPerDimension_NeverShrinksEither()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(1600, 720, Ms(0));

            // Taller but narrower consumer: width must not drop to 800.
            policy.Request(800, 900, Ms(100));
            Assert.True(policy.Request(800, 900, Ms(1100)));
            Assert.Equal((1600, 900), policy.Output);
        }

        [Fact]
        public void Grow_CombinesConcurrentLargerRequests()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(400, 180, Ms(0));

            policy.Request(1800, 600, Ms(100));
            policy.Request(1000, 800, Ms(500));
            Assert.True(policy.Request(1800, 600, Ms(1100)));
            Assert.Equal((1800, 800), policy.Output);
        }

        [Fact]
        public void StaleLargerRequest_DoesNotCountTowardGrowth()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);
            policy.Request(400, 180, Ms(0));

            // A one-off larger draw, then nothing larger for a while.
            Assert.False(policy.Request(1600, 720, Ms(100)));
            Assert.False(policy.Request(400, 180, Ms(2000)));

            // A new larger request starts a fresh one-second window.
            Assert.False(policy.Request(1600, 720, Ms(3000)));
            Assert.False(policy.Request(1600, 720, Ms(3500)));
            Assert.True(policy.Request(1600, 720, Ms(4000)));
        }

        [Fact]
        public void NonPositiveRequest_ClampsToOnePixel()
        {
            var policy = new VideoOutputSizePolicy(2400, 1080);

            policy.Request(0, -5, Ms(0));
            Assert.Equal((1, 1), policy.Output);
        }
    }
}

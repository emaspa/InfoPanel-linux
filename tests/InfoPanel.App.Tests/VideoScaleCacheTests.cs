using System;
using System.IO;
using InfoPanel.Video;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class VideoScaleCacheTests
    {
        private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

        [Theory]
        [InlineData(2400, 1080, 1600, 720, true)]    // 2.25x the pixels
        [InlineData(1920, 1080, 1600, 900, false)]   // 1.44x: not worth a lossy copy
        [InlineData(2400, 1080, 1601, 720, false)]   // odd width, not encodable as 4:2:0
        [InlineData(2400, 1080, 1600, 0, false)]
        public void CopiesOnlyClearlyLargerSources(int sw, int sh, int w, int h, bool expected)
        {
            Assert.Equal(expected, VideoScaleCache.IsWorthCaching(sw, sh, w, h, OneMinute));
        }

        [Fact]
        public void SkipsLongAndUnknownDurations()
        {
            Assert.False(VideoScaleCache.IsWorthCaching(3840, 2160, 1600, 720, null));
            Assert.False(VideoScaleCache.IsWorthCaching(3840, 2160, 1600, 720, TimeSpan.FromMinutes(11)));
        }

        [Fact]
        public void KeyChangesWithTheSourceAndSize()
        {
            var when = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
            var key = VideoScaleCache.Key("/v.mp4", 100, when, 1600, 720);

            Assert.Equal(32, key.Length);
            Assert.Equal(key, VideoScaleCache.Key("/v.mp4", 100, when, 1600, 720));
            Assert.NotEqual(key, VideoScaleCache.Key("/v.mp4", 100, when.AddSeconds(1), 1600, 720));
            Assert.NotEqual(key, VideoScaleCache.Key("/v.mp4", 101, when, 1600, 720));
            Assert.NotEqual(key, VideoScaleCache.Key("/w.mp4", 100, when, 1600, 720));
            Assert.NotEqual(key, VideoScaleCache.Key("/v.mp4", 100, when, 800, 360));
        }

        [Fact]
        public void ReturnsAnExistingCopyWithoutEncoding()
        {
            var dir = Directory.CreateTempSubdirectory("infopanel-video-cache-");
            try
            {
                var source = Path.Combine(dir.FullName, "source.mp4");
                File.WriteAllBytes(source, [1, 2, 3]);
                var info = new FileInfo(source);
                var cacheDir = Path.Combine(dir.FullName, "cache");
                Directory.CreateDirectory(cacheDir);
                var copy = Path.Combine(cacheDir, VideoScaleCache.Key(info.FullName, info.Length, info.LastWriteTimeUtc, 1600, 720) + ".mp4");
                File.WriteAllBytes(copy, [4]);

                var cache = new VideoScaleCache(cacheDir);
                var ready = false;
                Assert.Equal(copy, cache.GetOrStart(source, 2400, 1080, 1600, 720, OneMinute, () => ready = true, default));
                Assert.False(ready);
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        }

        [Fact]
        public void IgnoresUrlsAndSmallSources()
        {
            var cache = new VideoScaleCache(Path.Combine(Path.GetTempPath(), "infopanel-unused-cache"));
            Assert.Null(cache.GetOrStart("https://host/v.mp4", 2400, 1080, 1600, 720, OneMinute, () => { }, default));
            Assert.Null(cache.GetOrStart("/nonexistent/v.mp4", 1600, 720, 1600, 720, OneMinute, () => { }, default));
        }

        [Fact]
        public void PruneDeletesStaleTempFiles()
        {
            var dir = Directory.CreateTempSubdirectory("infopanel-video-prune-");
            try
            {
                var cache = new VideoScaleCache(dir.FullName);
                var stale = Path.Combine(dir.FullName, "a.mp4.1.tmp");
                File.WriteAllBytes(stale, [0]);
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
                var fresh = Path.Combine(dir.FullName, "b.mp4.1.tmp");
                File.WriteAllBytes(fresh, [0]);

                cache.Prune();

                Assert.False(File.Exists(stale));
                Assert.True(File.Exists(fresh));
            }
            finally
            {
                dir.Delete(recursive: true);
            }
        }
    }
}

using InfoPanel.Utils;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class NativeHeapTests
    {
        [Theory]
        [InlineData(null, "glibc.malloc.arena_max=2")]
        [InlineData("", "glibc.malloc.arena_max=2")]
        [InlineData("glibc.malloc.tcache_count=0", "glibc.malloc.tcache_count=0:glibc.malloc.arena_max=2")]
        [InlineData("glibc.malloc.arena_max=8", "glibc.malloc.arena_max=8")]
        public void ChildTunablesKeepTheUsersSettings(string? existing, string expected)
        {
            Assert.Equal(expected, NativeHeap.ChildTunables(existing));
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class SkiaNativeTests
    {
        // Directory.Build.targets swaps in a libSkiaSharp.so built without
        // retpolines (3.119.1): the stock 3.119.4 one makes CPU rendering several
        // times slower. A SkiaSharp/Avalonia bump could quietly bring it back.
        [Fact]
        public void LoadedSkiaNativeHasNoRetpolines()
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                return;
            }

            using (var bitmap = new SKBitmap(1, 1))
            {
                bitmap.Erase(SKColors.Black);
            }

            var path = File.ReadLines("/proc/self/maps")
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault())
                .FirstOrDefault(p => p != null && p.EndsWith("/libSkiaSharp.so", StringComparison.Ordinal));
            Assert.False(path == null, "libSkiaSharp.so is not mapped after using SkiaSharp");

            var bytes = File.ReadAllBytes(path!);
            var marker = Encoding.ASCII.GetBytes("__llvm_retpoline");
            Assert.True(bytes.AsSpan().IndexOf(marker) < 0,
                $"{path} is a retpoline build; check the UseRetpolineFreeSkiaNative target in Directory.Build.targets");
        }
    }
}

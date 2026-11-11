using System;
using System.Collections.Immutable;
using InfoPanel.Drawing;
using InfoPanel.Gpu;
using InfoPanel.Models;
using SkiaSharp;
using Xunit;

namespace InfoPanel.App.Tests
{
    [Collection(nameof(GpuRenderingTests))]
    public class GpuRenderingTests
    {
        [Theory]
        [InlineData("NVIDIA GeForce RTX 5090/PCIe/SSE2", "nvidia", "NVIDIA GeForce RTX 5090")]
        [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor (radeonsi, raphael_mendocino, ACO, DRM 3.64, 7.3.3-1-cachyos)", "amdgpu", "AMD Ryzen 9 9950X3D 16-Core Processor [raphael_mendocino]")]
        [InlineData("Mesa Intel(R) Graphics (ADL GT2)", "i915", "Mesa Intel(R) Graphics [ADL GT2]")]
        [InlineData(null, "amdgpu", "AMD GPU")]
        [InlineData("", "xe", "Intel GPU")]
        public void CleansRendererNames(string? renderer, string driver, string expected)
        {
            Assert.Equal(expected, GpuDeviceCatalog.CleanName(renderer, driver));
        }

        [Fact]
        public void MissingDeviceFallsBackToTheCpu()
        {
            try
            {
                GpuRendering.Select("pci:ffff:ff:ff.f");
                using var bitmap = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Opaque);
                var profile = new Profile { Guid = Guid.NewGuid(), Width = 8, Height = 8 };
                Assert.False(GpuRendering.TryRender(profile, bitmap, "test"));
                Assert.Contains("not found", GpuRendering.Status);
                Assert.False(GpuRendering.IsActive);
            }
            finally
            {
                GpuRendering.Select(GpuRendering.Cpu);
            }
        }

        // Runs on every GPU this machine exposes (none on CI): the frame must come
        // back with the right size, channel order and content.
        [Fact]
        public void RendersTheSameFrameOnEveryGpu()
        {
            var profile = new Profile { Guid = Guid.NewGuid(), Width = 320, Height = 80, BackgroundColor = "#FF336699" };
            var previous = RenderContext.GetDisplayItems;
            RenderContext.GetDisplayItems = p => p.Guid == profile.Guid
                ? [new TextDisplayItem("GPU 42", profile) { X = 10, Y = 10, FontSize = 30, Color = "#FFFFCC00" }]
                : previous(p);
            try
            {
                using var cpu = new SKBitmap(160, 40, SKColorType.Rgba8888, SKAlphaType.Opaque);
                using (var g = SkiaGraphics.FromBitmap(cpu, profile.FontScale))
                {
                    g.Canvas.Scale(0.5f, 0.5f);
                    PanelDraw.Run(profile, g, cacheHint: "gpu-test");
                }

                foreach (var device in GpuDeviceCatalog.Devices)
                {
                    GpuRendering.Select(device.Id);
                    using var gpu = new SKBitmap(160, 40, SKColorType.Rgba8888, SKAlphaType.Opaque);
                    Assert.True(GpuRendering.TryRender(profile, gpu, "gpu-test"), $"{device.DisplayName}: {GpuRendering.Status}");

                    // Background pixel exact (catches a BGRA/RGBA swap), text close.
                    Assert.Equal(new SKColor(0x33, 0x66, 0x99), gpu.GetPixel(155, 35));
                    long diff = 0;
                    for (int y = 0; y < 40; y++)
                    {
                        for (int x = 0; x < 160; x++)
                        {
                            var a = cpu.GetPixel(x, y);
                            var b = gpu.GetPixel(x, y);
                            diff += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
                        }
                    }
                    Assert.True(diff / (160.0 * 40 * 3) < 3, $"{device.DisplayName}: mean channel difference {diff / (160.0 * 40 * 3):F2}");
                }
            }
            finally
            {
                GpuRendering.Select(GpuRendering.Cpu);
                RenderContext.GetDisplayItems = previous;
            }
        }
    }
}

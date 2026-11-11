using InfoPanel.Video;
using Xunit;

namespace InfoPanel.App.Tests
{
    public class VideoHardwareDecoderTests
    {
        private const long GiB = 1L << 30;
        private static readonly RenderNode Nvidia = new("/dev/dri/renderD128", "0000:01:00.0", "nvidia", 0);
        private static readonly RenderNode AmdIgpu = new("/dev/dri/renderD129", "0000:7d:00.0", "amdgpu", 2 * GiB);
        private static readonly RenderNode AmdDgpu = new("/dev/dri/renderD130", "0000:03:00.0", "amdgpu", 16 * GiB);
        private static readonly RenderNode Intel = new("/dev/dri/renderD131", "0000:00:02.0", "i915", 0);

        [Fact]
        public void PrefersTheIntegratedGpuOverNvidia()
        {
            var hw = VideoHardwareDecoder.Choose([Nvidia, AmdIgpu], "cpu");
            Assert.Equal(new HardwareDecoder(HardwareDecodeApi.Vaapi, AmdIgpu.Path), hw with { Description = null });
        }

        [Fact]
        public void PrefersTheSmallerVramAmongOthers()
        {
            Assert.Equal(AmdIgpu.Path, VideoHardwareDecoder.Choose([AmdDgpu, AmdIgpu], null).Device);
            Assert.Equal(Intel.Path, VideoHardwareDecoder.Choose([AmdDgpu, Intel], null).Device);
        }

        [Fact]
        public void FollowsThePanelRenderGpu()
        {
            Assert.Equal(HardwareDecodeApi.Cuda, VideoHardwareDecoder.Choose([Nvidia, AmdIgpu], "pci:0000:01:00.0").Api);
            Assert.Equal(AmdDgpu.Path, VideoHardwareDecoder.Choose([AmdIgpu, AmdDgpu], "PCI:0000:03:00.0").Device);
        }

        [Fact]
        public void UnknownRenderGpuFallsBackToTheDefaultOrder()
        {
            Assert.Equal(AmdIgpu.Path, VideoHardwareDecoder.Choose([Nvidia, AmdIgpu], "pci:0000:99:00.0").Device);
        }

        [Fact]
        public void NvidiaOnlyUsesCuda_NoNodesUsesAuto()
        {
            Assert.Equal(HardwareDecodeApi.Cuda, VideoHardwareDecoder.Choose([Nvidia], null).Api);
            Assert.Equal(HardwareDecodeApi.Auto, VideoHardwareDecoder.Choose([], null).Api);
        }
    }
}

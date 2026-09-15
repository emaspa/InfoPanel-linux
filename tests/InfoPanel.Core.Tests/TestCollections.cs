using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Sensors;
using Xunit;

namespace InfoPanel.Core.Tests;

[CollectionDefinition("ConfigPersistence", DisableParallelization = true)]
public sealed class CoreStateCollection : ICollectionFixture<SensorStateFixture>;

public sealed class SensorStateFixture : IDisposable
{
    public const string WireViewId = "hwmon/v1/wireview/platform+wireview_hwmon/curr5";

    public void Reset()
    {
        ConfigPersistence.PostLoadHook = null;
        ConfigPersistence.BaseFolderOverride = null;
        SensorReader.ResetForTests();
        SensorDemand.ResetForTests();
        SensorBindingMigration.ResetForTests();
    }

    public void Dispose() => Reset();

    public static SensorDescriptor WireView => new(SensorId.Parse(WireViewId))
    {
        Label = "Pin 5", RawLabel = "Pin 5", IsLabelSynthetic = false,
        LegacyAliases = ["hwmon4/curr5"]
    };

    public static SensorIdResolver PublishWireView()
    {
        var resolver = new SensorIdResolver();
        resolver.Publish(new(1, [WireView]));
        SensorReader.ConfigureResolver(resolver);
        return resolver;
    }

    public static void EnableMigration() =>
        ConfigPersistence.PostLoadHook = (_, items) => SensorBindingMigration.Migrate(items);
}

public abstract class SensorStateTest : IDisposable
{
    private readonly SensorStateFixture _fixture;

    protected SensorStateTest(SensorStateFixture fixture)
    {
        _fixture = fixture;
        fixture.Reset();
    }

    public virtual void Dispose() => _fixture.Reset();
}

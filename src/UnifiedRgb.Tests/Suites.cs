namespace UnifiedRgb.Tests;

/*-----------------------------------------------------------*\
| The suite table: the one place that says what exists and     |
| what order it runs in.                                       |
|                                                              |
| Adding tests = a new file under Suites/ and one line here.   |
| The name is what `--filter` matches and what a failure line  |
| is tagged with, so keep it short and specific.               |
|                                                              |
| Order is deliberate: the cheap pure-logic suites run first,  |
| so a broken build of the basics fails in milliseconds rather |
| than behind half a minute of socket and sleep-driven ones.   |
\*-----------------------------------------------------------*/
static class Suites
{
    public static readonly (string Name, Action<Harness> Run)[] All =
    {
        // Pure logic: color maths, codecs, file formats, curves.
        ("Color",         ColorSuite.Run),
        ("Calibration",   CalibrationSuite.Run),
        ("Codec",         CodecSuite.Run),
        ("Storage",       StorageSuite.Run),
        ("Profiles",      ProfilesSuite.Run),
        ("ProfileEditing", ProfileEditingSuite.Run),
        ("ProfileBindings", ProfileBindingSuite.Run),
        ("Bindings",      BindingsSuite.Run),
        ("PreviewCache",  PreviewCacheSuite.Run),
        ("Closing",       ClosingSuite.Run),
        ("Backup",        BackupSuite.Run),
        ("FixBundle",     FixBundleSuite.Run),
        ("Fans",          FansSuite.Run),
        ("SensorRules",   SensorRulesSuite.Run),
        ("SensorHub",     SensorHubSuite.Run),
        ("FixFans",       FixFansSuite.Run),
        ("InvariantFans", InvariantFansSuite.Run),
        ("WireView",      WireViewSuite.Run),

        // The lighting engine and everything that renders.
        ("Effects",       EffectsSuite.Run),
        ("Canvas",        CanvasSuite.Run),
        ("CanvasLoad",    CanvasLoadSuite.Run),
        ("Undo",          UndoSuite.Run),
        ("LcdScenes",     LcdScenesSuite.Run),
        ("PanelWedge",    PanelWedgeSuite.Run),

        // Drivers: real protocol bytes over FakeHid.
        ("Devices",       DevicesSuite.Run),
        ("GigabyteDriver", GigabyteDriverSuite.Run),
        ("LianBake",      LianBakeSuite.Run),
        ("ReviewFixes",   ReviewFixesSuite.Run),
        ("WriteContract", WriteContractSuite.Run),
        ("FixDrivers",    FixDriversSuite.Run),
        ("RemainingDrivers", RemainingDriversSuite.Run),
        ("DeviceHealth",  DeviceHealthSuite.Run),
        ("Battery",       BatterySuite.Run),
        ("HardwareExit",  HardwareExitSuite.Run),
        ("Native",        NativeSuite.Run),
        ("WireViewLifecycle", WireViewLifecycleSuite.Run),

        // Write ordering between the engine and the applier lanes.
        ("EngineLighting", EngineLightingSuite.Run),
        ("FixLighting",   FixLightingSuite.Run),

        // Automation, scheduling and the app's own rules.
        ("Automation",    AutomationSuite.Run),
        ("Activity",      ActivitySuite.Run),
        ("FixState",      FixStateSuite.Run),
        ("RemainingState", RemainingStateSuite.Run),
        ("InvariantLightsHw", InvariantLightsHwSuite.Run),
        ("InvariantLightsAsync", InvariantLightsAsyncSuite.Run),
        ("LightsOffBoundary", LightsOffBoundarySuite.Run),

        // Anything that opens a socket, a pipe or a process comes last.
        ("Chroma",        ChromaSuite.Run),
        ("Games",         GamesSuite.Run),
        ("Net",           NetSuite.Run),
        ("OpenRgbBridge", OpenRgbBridgeSuite.Run),
        ("SupportBundle", SupportBundleSuite.Run),
        ("FixMisc",       FixMiscSuite.Run),
        ("AppInstall",    AppInstallSuite.Run),
        ("RemainingServers", RemainingServersSuite.Run),

        // Reads the SOURCE, so it depends on nothing and goes last.
        ("SilentFailure", SilentFailureSuite.Run),
    };
}

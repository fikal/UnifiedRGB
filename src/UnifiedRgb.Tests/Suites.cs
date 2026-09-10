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
        // Pure logic: colour maths, codecs, file formats, curves.
        ("Color",         ColorSuite.Run),
        ("Codec",         CodecSuite.Run),
        ("Storage",       StorageSuite.Run),
        ("Profiles",      ProfilesSuite.Run),
        ("Fans",          FansSuite.Run),
        ("SensorRules",   SensorRulesSuite.Run),
        ("SensorHub",     SensorHubSuite.Run),

        // The lighting engine and everything that renders.
        ("Effects",       EffectsSuite.Run),
        ("Canvas",        CanvasSuite.Run),
        ("CanvasLoad",    CanvasLoadSuite.Run),
        ("Undo",          UndoSuite.Run),

        // Drivers: real protocol bytes over FakeHid.
        ("Devices",       DevicesSuite.Run),
        ("GigabyteDriver", GigabyteDriverSuite.Run),
        ("LianBake",      LianBakeSuite.Run),
        ("Battery",       BatterySuite.Run),
        ("HardwareExit",  HardwareExitSuite.Run),
        ("Native",        NativeSuite.Run),

        // Write ordering between the engine and the applier lanes.
        ("EngineLighting", EngineLightingSuite.Run),

        // Automation, scheduling and the app's own rules.
        ("Automation",    AutomationSuite.Run),

        // Anything that opens a socket, a pipe or a process comes last.
        ("Chroma",        ChromaSuite.Run),
        ("Games",         GamesSuite.Run),
        ("Net",           NetSuite.Run),
        ("OpenRgbBridge", OpenRgbBridgeSuite.Run),
        ("SupportBundle", SupportBundleSuite.Run),
    };
}

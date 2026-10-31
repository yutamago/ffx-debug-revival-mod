// SPDX-License-Identifier: MIT

using Yuta.DebugRevival.Features;
using Yuta.DebugRevival.Native;
using Yuta.DebugRevival.Script;
using Yuta.DebugRevival.Ui;

namespace Yuta.DebugRevival;

/// <summary>Debug Revival: the developers' debug tools left in FFX.exe, turned back on. F9 opens the window.</summary>
[FhLoad(FhGameId.FFX)]
public sealed class DebugRevivalModule : FhModule {
    private ScriptDebugger? _scripts;
    private TweakPanels?    _panels;
    private BattleDebug?    _battle;
    private FieldTools?     _field;
    private DebugLog?       _log;
    private Guards?         _guards;
    private DebugFlag?      _flag;
    private GuToolkit?      _gu;
    private GuRenderer?     _gu_renderer;
    private MainWindow?     _window;

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        NativeNames names = NativeNames.parse(resource("natives.tsv"));
        Dictionary<int, FieldTools.MapInfo> map_info = FieldTools.parse_map_table(resource("maps.tsv"));

        DirectoryInfo resources = mod_context.Paths.ResourcesDir;
        if (!resources.Exists) resources.Create();
        string font_path = Path.Combine(resources.FullName, "debugfont.generated.tga");

        _guards  = new Guards(_logger);
        _flag    = new DebugFlag(_guards);
        _scripts = new ScriptDebugger(_logger, names);
        _panels  = new TweakPanels(_guards, _flag);
        _battle  = new BattleDebug();
        _field   = new FieldTools(_logger, _guards, _flag, map_info);
        _log     = new DebugLog();
        _gu      = new GuToolkit(_flag);
        _gu_renderer = new GuRenderer(_gu, font_path);
        _window  = new MainWindow(new ScriptDebuggerUi(_scripts), _panels, _battle, _field, _log, _guards, _flag, _gu);

        if (!_guards.init(this)) {
            _logger.Error("Could not install the safety hooks; Debug Revival stays off.");
            return false;
        }
        // Any other part that fails to hook is left out; the rest still works.
        warn_unless(_scripts.init(this), "script debugger");
        warn_unless(_battle.init(this),  "battle status line");
        warn_unless(_field.init(this),   "noclip and developer saves");
        warn_unless(_log.init(this),     "debug log");
        warn_unless(_gu.init(this),      "PS2 debug menu");
        _flag.hooks_ok = _flag.init(this);
        warn_unless(_flag.hooks_ok, "debug flag's safety hooks (the flag stays off)");

        FhApi.Events.Common.GameLoop.PreUpdate.subscribe(pre_update);
        FhApi.Events.Common.GameLoop.PostUpdate.subscribe(post_update);
        return true;
    }

    private static string resource(string name) {
        using Stream s = typeof(DebugRevivalModule).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"{name} is missing from the assembly");
        using StreamReader r = new(s);
        return r.ReadToEnd();
    }

    private void warn_unless(bool ok, string what) {
        if (!ok) _logger.Warning($"Could not hook the {what}; that part will not work.");
    }

    private void pre_update(UpdateLoopEventArgs args) {
        _guards!.verify();
        GameThread.drain(_logger);
        try { _panels!.update(); }
        catch (Exception e) { _logger.Error($"panel input: {e}"); _panels!.input_enabled = false; }
    }

    private void post_update(UpdateLoopEventArgs args) {
        try {
            _flag!.update();
            _battle!.update();
            _field!.update();
            _panels!.snapshot();
        }
        catch (Exception e) { _logger.Error($"snapshot: {e}"); }
    }

    private bool _ui_failed;

    public override void render_imgui() {
        // Fahrenheit calls this without a try/catch, so an exception would end the game.
        if (_ui_failed) return;
        try {
            _gu_renderer?.draw();
            _window?.draw();
        }
        catch (Exception e) {
            _ui_failed = true;
            _logger.Error($"The window failed and is now off: {e}");
        }
    }
}

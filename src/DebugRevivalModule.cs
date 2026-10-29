// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Features;
using Yuta.DebugRevival.Native;
using Yuta.DebugRevival.Script;
using Yuta.DebugRevival.Ui;

namespace Yuta.DebugRevival;

/// <summary>
///     Debug Revival: the developers' debug tools left in FFX.exe, turned back on.
///     Press F9 for the window.
/// </summary>
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

    public DebugRevivalModule() { }

    public override bool init(FhModContext mod_context, FileStream global_state_file) {
        NativeNames names;
        using (Stream? s = typeof(DebugRevivalModule).Assembly.GetManifestResourceStream("natives.tsv")) {
            using StreamReader r = new(s ?? throw new InvalidOperationException("natives.tsv is missing from the assembly"));
            names = NativeNames.parse(r.ReadToEnd());
        }

        // The debug font is decoded from the player's FFX.exe at runtime into the mod's resources folder.
        DirectoryInfo resources = mod_context.Paths.ResourcesDir;
        if (!resources.Exists) resources.Create();
        string font_path = Path.Combine(resources.FullName, "debugfont.generated.tga");

        _guards  = new Guards(_logger);
        _flag    = new DebugFlag();
        _scripts = new ScriptDebugger(_logger, names);
        _panels  = new TweakPanels(_guards, _flag);
        _battle  = new BattleDebug();
        _field   = new FieldTools(_guards, _flag);
        _log     = new DebugLog();
        _gu      = new GuToolkit(_flag);
        _gu_renderer = new GuRenderer(_gu, font_path);
        _window  = new MainWindow(new ScriptDebuggerUi(_scripts), _panels, _battle, _field, _log, _guards, _flag, _gu);

        // The guards come first: without them the panels must not run.
        if (!_guards.init(this)) {
            _logger.Error("Could not install the safety hooks; Debug Revival stays off.");
            return false;
        }
        bool ok = true;
        ok &= report(_scripts.init(this), "script debugger");
        ok &= report(_battle.init(this),  "battle status line");
        ok &= report(_field.init(this),   "noclip");
        ok &= report(_log.init(this),     "debug log");
        ok &= report(_gu.init(this),      "PS2 debug menu");

        FhApi.Events.Common.GameLoop.PreUpdate.subscribe(pre_update);
        FhApi.Events.Common.GameLoop.PostUpdate.subscribe(post_update);
        return ok;
    }

    private bool report(bool ok, string what) {
        if (!ok) _logger.Warning($"Could not hook the {what}; that part will not work.");
        return true; // the rest still works
    }

    private bool _guards_checked;

    private void pre_update(UpdateLoopEventArgs args) {
        if (!_guards_checked) { _guards!.verify(); _guards_checked = true; }
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
        // Fahrenheit calls this without a try/catch; an exception here would end the game.
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

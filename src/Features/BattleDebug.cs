// SPDX-License-Identifier: MIT

using Fahrenheit.FFX.Battle;
using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Battle debug: the BattleConfig window's switches, which the battle code still reads without checking the debug
///     flag, the Ms debug cheats, the scene picker and an actor inspector. docs/research/battle.md has the evidence for each.
/// </summary>
public sealed unsafe class BattleDebug {
    public enum Risk { None, Stalls, Progress, Untested }

    /// <param name="Address">Live address of the switch byte (or word, with <paramref name="IsU32"/>).</param>
    public sealed record Toggle(string Group, string Label, string Effect, nint Address, bool IsU32 = false, Risk Risk = Risk.None);

    // Fahrenheit's battle struct; the switches it names come from its BtlDebugFlags (Btl.debug), the rest by address.
    private static Btl* B => Fahrenheit.FFX.Globals.Battle.btl;
    private static nint flag(bool* p) => (nint)p;

    public static readonly Toggle[] TOGGLES = [
        new("Damage", "No damage variance", "damage without its random spread", flag(&B->debug.no_variance)),
        new("Damage", "Never critical", "no critical hits", flag(&B->debug.never_crit)),
        new("Damage", "Always critical", "every hit is critical, unless Never critical is on", flag(&B->debug.always_crit)),
        new("Damage", "Always hit", "attacks never miss; steals, bribes, statuses and drops always work", flag(&B->debug.always_hit)),
        new("Damage", "Always miss", "every attack and status misses and escapes fail, for both sides", flag(&B->debug.never_hit)),
        new("Damage", "No status effects", "nobody gets a status effect", Mem.at(0x0112a906)),
        new("Damage", "No overkill", "kills never count as overkills", flag(&B->debug.never_overkill)),
        new("Damage", "Every result = 1", "every damage and heal is 1", flag(&B->debug.always_1_dmg)),
        new("Damage", "Every result = 9999", "every damage and heal is 10000, then capped", flag(&B->debug.always_9999_dmg)),
        new("Damage", "Every result = 99999", "every damage and heal is 100000, then capped", flag(&B->debug.always_99999_dmg)),

        new("Survival", "Party invincible", "the party loses no HP or MP", flag(&B->debug.invincible_ply)),
        new("Survival", "Monsters invincible", "monsters lose no HP or MP", flag(&B->debug.invincible_mon)),
        new("Survival", "Main characters never die", "the seven main characters take no damage; everyone else dies in one hit", Mem.at(0x0113338c), IsU32: true),
        new("Survival", "Party enters with 1 HP", "party members start the next battle at 1 HP", Mem.at(0x0112a91d)),
        new("Survival", "Monsters enter with 1 HP", "monsters start the next battle at 1 HP", Mem.at(0x0112a91e)),

        new("Resources", "Free MP", "commands cost no MP", flag(&B->debug.no_mp_cost)),
        new("Resources", "Overdrive always ready", "overdrives work with any gauge", flag(&B->debug.always_available_overdrive)),
        new("Resources", "Overdrive gauge frozen", "the overdrive gauge never fills", flag(&B->debug.never_charge_overdrive)),

        new("Rewards", "Always rare steal/drop", "steals and drops give the rare item", flag(&B->debug.always_rare_steal)),
        new("Rewards", "AP x100", "100 times the AP after the battle", flag(&B->debug.ap_x100)),
        new("Rewards", "Gil x100", "100 times the gil after the battle", flag(&B->debug.gil_x100)),

        new("Flow", "Control the monsters", "you choose the monsters' commands", flag(&B->debug.mon_control)),
        new("Flow", "CTB same order", "turns go round in a fixed order", Mem.at(0x0112a8fb)),
        new("Flow", "Skip spell effects", "spells and abilities without their animation", flag(&B->debug.no_magic_effects)),
        new("Flow", "Summon without effect", "aeons are summoned without their usual effect", Mem.at(0x0112a904)),
        new("Flow", "Always sensor", "enemy info is always shown, as with Sensor", flag(&B->debug.permanent_sensor)),
        new("Flow", "Pause the CTB", "nobody gets a turn while on", Mem.at(0x0112a8e1), Risk: Risk.Stalls),
        new("Flow", "Hold effects", "the next command's animation waits until this is off", Mem.at(0x0112a91c), Risk: Risk.Stalls),
        new("Flow", "Skip every command", "the developers' 'Skip Command'; may hide every command", Mem.at(0x0112a922), Risk: Risk.Untested),

        new("Camera", "Battle camera off", "the automatic battle camera stops moving", flag(&B->debug.free_camera)),
        new("Camera", "No head tracking", "characters stop turning their heads", Mem.at(0x0112a926)),
        new("Camera", "No focus reset", "the view doesn't re-centre after a special camera", Mem.at(0x0112a927)),
        new("Camera", "Camera priority mask", "from the next battle, scripted camera changes are ignored", Mem.at(0x0112a924)),

        new("Developer", "Discard battle results", "HP, MP and AP from the battle aren't kept", Mem.at(0x0112a91a), Risk: Risk.Progress),
        new("Developer", "Slow battle loading", "each loading step waits about a second", Mem.at(0x0112a918)),
        new("Developer", "Every spell plays effect 0", "every command shows the developers' test effect", Mem.at(0x0112a902)),
        new("Developer", "Event effects use file 0", "scripted effects show the developers' test effect", Mem.at(0x0112a903)),
        new("Developer", "Test hit effects", "a developer test for hit effects", Mem.at(0x0112a917)),
        new("Developer", "Map circle", "shows a test circle when a battle map loads", Mem.at(0x0112a91b)),
        new("Developer", "Invert 'will die'", "enemy AI's 'about to die' check gives the opposite answer", Mem.at(0x0112a907)),
        new("Developer", "Print voice info", "writes voice lines to the Log tab", Mem.at(0x0112a90b)),
        new("Developer", "New game: all weapons", "a new game hands out extra equipment", Mem.at(0x0112a920)),
    ];

    /// <param name="ReadsDisc">Reads a file synchronously, so it must not run while a battle loads or ends.</param>
    private static sbyte battle_state => (sbyte)B->battle_state;

    public sealed record Cheat(uint Va, string Label, string Effect, bool SaveChanging, bool BattleOnly = false, bool StoryBreaking = false, bool ReadsDisc = false);

    public static readonly Cheat[] CHEATS = [
        new(0x007849d0, "All items x99", "99 of every item", true),
        new(0x007849c0, "Max gil", "999,999,999 gil", true),
        new(0x007848b0, "All abilities", "every character learns every ability", true, ReadsDisc: true),
        new(0x00784ae0, "98 sphere levels", "every character gets 98 sphere levels", true),
        new(0x00784b00, "Max stats", "HP 99999, MP 9999, every other stat 255", true),
        new(0x00784c40, "Overdrive modes almost learnt", "every overdrive mode in progress needs one more action", true),
        new(0x00784c20, "Every item x1", "1 of every item, replacing your counts", true),
        new(0x00784cd0, "Empty inventory", "removes every item", true),
        new(0x00784ac0, "All party members", "every character and aeon joins now", true, StoryBreaking: true),
        new(0x00784b60, "All aeons", "every aeon joins now", true, StoryBreaking: true),
        new(0x00784b80, "Everyone to 1 HP", "everyone in the battle, party included", false, BattleOnly: true),
    ];

    private const uint MS_DEBUG_WEAK_MONSTERS = 0x00784bc0;
    private const uint MS_DEBUG_WEAK_PARTY    = 0x00784c80;
    private const uint MS_DEBUG_SCENE_WALK    = 0x007c5dd0;
    private const uint TO_ALL_CLEAR_MESSAGE_WINDOW = 0x008ab590;

    public sealed record ActorRow(int Index, ushort ChrId, int Hp, int MaxHp, int Mp, int MaxMp,
                                  byte Str, byte Def, byte Mag, byte MDef, byte Agi, byte Luck, byte Eva, byte Acc,
                                  byte OdGauge, byte OdMax, byte Ctb, ushort Status, ushort Status2, byte Ko);

    public sealed record Snapshot(sbyte BtlStatus, uint[] ToggleValues, ActorRow[] Actors);

    public volatile Snapshot? snap;
    public volatile string picker_result = "";
    private uint _picker_label;

    public volatile bool native_status_line;
    private long _status_frame = -1;
    private long _frame;

    public bool init(FhModule owner) => Calls.TODapsDraw.hook(owner, h_todaps_draw);

    /// <summary>Game thread, after each update.</summary>
    public void update() {
        _frame++;
        uint[] values = new uint[TOGGLES.Length];
        for (int i = 0; i < TOGGLES.Length; i++) {
            values[i] = TOGGLES[i].IsU32 ? *(uint*)TOGGLES[i].Address : *(byte*)TOGGLES[i].Address;
        }
        List<ActorRow> rows = [];
        nint top = *(nint*)Mem.at(G.BtlChrTop);
        sbyte status = battle_state;
        if (top != 0 && status != 0) {
            for (int i = 0; i < 0x1f; i++) {
                nint a = top + i * G.ActorSize;
                if (*(byte*)(a + 0xdc8) == 0) continue; // not taking part
                rows.Add(new ActorRow(i, *(ushort*)(a + 0xe),
                    *(int*)(a + 0x5d0), *(int*)(a + 0x594), *(int*)(a + 0x5d4), *(int*)(a + 0x598),
                    *(byte*)(a + 0x5a8), *(byte*)(a + 0x5a9), *(byte*)(a + 0x5aa), *(byte*)(a + 0x5ab),
                    *(byte*)(a + 0x5ac), *(byte*)(a + 0x5ad), *(byte*)(a + 0x5ae), *(byte*)(a + 0x5af),
                    *(byte*)(a + 0x5bc), *(byte*)(a + 0x5bd), *(byte*)(a + 0x65c),
                    *(ushort*)(a + 0x606), *(ushort*)(a + 0x616), *(byte*)(a + 0xdcc)));
            }
        }
        snap = new Snapshot(status, values, [ .. rows ]);
    }

    public void set_toggle(int i, bool on) {
        Toggle t = TOGGLES[i];
        GameThread.post(t.Label, () => {
            if (t.IsU32) *(uint*)t.Address = on ? 1u : 0u;
            else *(byte*)t.Address = on ? (byte)1 : (byte)0;
        });
    }

    public void run_cheat(Cheat c) => GameThread.post(c.Label, () => {
        sbyte status = battle_state;
        if (c.BattleOnly && status == 0) { Status.refused($"{c.Label}: only in battle"); return; }
        if (c.ReadsDisc && status is not (0 or 1)) { Status.refused($"{c.Label}: wait until the battle is running"); return; }
        Calls.void_fn(c.Va).fnptr!();
        Status.done($"done: {c.Label}");
    });

    public void weak(bool monsters, bool on) => GameThread.post(monsters ? "monsters HP" : "party HP", () => {
        if (battle_state == 0) { Status.refused("only in battle"); return; }
        Calls.int_fn(monsters ? MS_DEBUG_WEAK_MONSTERS : MS_DEBUG_WEAK_PARTY).fnptr!(on ? 1 : 0);
        Status.done((monsters ? "monsters" : "party") + (on ? " at 1 HP" : " at full HP"));
    });

    /// <summary>Writes an actor field (i32 or u8) if the battle is still the same.</summary>
    public void set_actor(int index, int offset, int value, bool is_byte) => GameThread.post("edit actor", () => {
        nint top = *(nint*)Mem.at(G.BtlChrTop);
        if (top == 0 || battle_state == 0) { Status.refused("the battle is over"); return; }
        nint a = top + index * G.ActorSize + offset;
        if (is_byte) *(byte*)a = (byte)Math.Clamp(value, 0, 255);
        else *(int*)a = value;
    });

    /// <summary>Sets the developers' scene picker and reads back the battle it names.</summary>
    public void pick(int field, int encounter) => GameThread.post("pick battle", () => {
        Mem.set_u32(G.PickerField, (uint)field);
        Mem.set_u32(G.PickerEncounter, (uint)encounter);
        Calls.void_fn(MS_DEBUG_SCENE_WALK).fnptr!();
        nint buf = Marshal.AllocHGlobal(64);
        try {
            new Span<byte>((void*)buf, 64).Clear();
            Calls.MsGetBattleName.fnptr!(0, buf);
            _picker_label = (uint)Calls.MsDebugGetFieldNum.fnptr!();
            picker_result = Mem.cstr(buf, 63);
        }
        finally { Marshal.FreeHGlobal(buf); }
    });

    /// <summary>Starts the picked battle the way the developers' L1+R1+L2+R2+X combo did.</summary>
    public void start_picked() => GameThread.post("start battle", () => {
        if (_picker_label == 0) { Status.refused("look up a battle first"); return; }
        if (battle_state != 0) { Status.refused("already in a battle"); return; }
        if (!FieldTools.jump_allowed()) { Status.refused("only while you control the player in the field"); return; }
        if (FieldTools.effect_active) { Status.refused("wait until the effect on screen has finished"); return; }
        Calls.void_fn(TO_ALL_CLEAR_MESSAGE_WINDOW).fnptr!();
        Calls.MsBattleLabelExe.fnptr!(_picker_label, 0, 0);
        if (B->battle_trigger == 2) Status.done("battle requested");
        else Status.refused("the game refused the battle");
    });

    private D.VoidFn? _next_todaps_draw;

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_todaps_draw() {
        // TODapsMakePkt, which draws the debug text buffer, lost its call; run it before TODapsDraw clears the buffer.
        if (native_status_line && _status_frame != _frame && battle_state != 0) {
            _status_frame = _frame;
            Calls.MsBattleProcess_DebugStatus.fnptr!();
            Calls.TODapsMakePkt.fnptr!();
        }
        _next_todaps_draw ??= Calls.TODapsDraw.chain_from(h_todaps_draw).fnptr!;
        _next_todaps_draw();
    }
}

// SPDX-License-Identifier: MIT OR Apache-2.0

using Yuta.DebugRevival.Native;

namespace Yuta.DebugRevival.Features;

/// <summary>
///     Battle debug: the BattleConfig window's switches, which the battle code still reads without checking the debug
///     flag, the Ms debug cheats, the scene picker and an actor inspector. docs/research/battle.md has the evidence for each.
/// </summary>
public sealed unsafe class BattleDebug {
    public enum Risk { None, Stalls, Progress, Untested }

    public sealed record Toggle(uint Va, string Group, string Label, string Effect, bool IsU32 = false, Risk Risk = Risk.None);

    public static readonly Toggle[] TOGGLES = [
        new(0x0112a908, "Damage", "No damage variance", "damage without its random spread"),
        new(0x0112a909, "Damage", "Never critical", "no critical hits"),
        new(0x0112a90d, "Damage", "Always critical", "every hit is critical, unless Never critical is on"),
        new(0x0112a90a, "Damage", "Always hit", "attacks never miss; steals, bribes, statuses and drops always work"),
        new(0x0112a91f, "Damage", "Always miss", "every attack and status misses and escapes fail, for both sides"),
        new(0x0112a906, "Damage", "No status effects", "nobody gets a status effect"),
        new(0x0112a914, "Damage", "No overkill", "kills never count as overkills"),
        new(0x0112a90e, "Damage", "Every result = 1", "every damage and heal is 1"),
        new(0x0112a90f, "Damage", "Every result = 9999", "every damage and heal is 10000, then capped"),
        new(0x0112a910, "Damage", "Every result = 99999", "every damage and heal is 100000, then capped"),

        new(0x0112a8f9, "Survival", "Party invincible", "the party loses no HP or MP"),
        new(0x0112a8f8, "Survival", "Monsters invincible", "monsters lose no HP or MP"),
        new(0x0113338c, "Survival", "Main characters never die", "the seven main characters take no damage; everyone else dies in one hit", IsU32: true),
        new(0x0112a91d, "Survival", "Party enters with 1 HP", "party members start the next battle at 1 HP"),
        new(0x0112a91e, "Survival", "Monsters enter with 1 HP", "monsters start the next battle at 1 HP"),

        new(0x0112a901, "Resources", "Free MP", "commands cost no MP"),
        new(0x0112a90c, "Resources", "Overdrive always ready", "overdrives work with any gauge"),
        new(0x0112a916, "Resources", "Overdrive gauge frozen", "the overdrive gauge never fills"),

        new(0x0112a911, "Rewards", "Always rare steal/drop", "steals and drops give the rare item"),
        new(0x0112a912, "Rewards", "AP x100", "100 times the AP after the battle"),
        new(0x0112a913, "Rewards", "Gil x100", "100 times the gil after the battle"),

        new(0x0112a8fa, "Flow", "Control the monsters", "you choose the monsters' commands"),
        new(0x0112a8fb, "Flow", "CTB same order", "turns go round in a fixed order"),
        new(0x0112a900, "Flow", "Skip spell effects", "spells and abilities without their animation"),
        new(0x0112a904, "Flow", "Summon without effect", "aeons are summoned without their usual effect"),
        new(0x0112a915, "Flow", "Always sensor", "enemy info is always shown, as with Sensor"),
        new(0x0112a8e1, "Flow", "Pause the CTB", "nobody gets a turn while on", Risk: Risk.Stalls),
        new(0x0112a91c, "Flow", "Hold effects", "the next command's animation waits until this is off", Risk: Risk.Stalls),
        new(0x0112a922, "Flow", "Skip every command", "the developers' 'Skip Command'; may hide every command", Risk: Risk.Untested),

        new(0x0112a8fc, "Camera", "Battle camera off", "the automatic battle camera stops moving"),
        new(0x0112a926, "Camera", "No head tracking", "characters stop turning their heads"),
        new(0x0112a927, "Camera", "No focus reset", "the view doesn't re-centre after a special camera"),
        new(0x0112a924, "Camera", "Camera priority mask", "from the next battle, scripted camera changes are ignored"),

        new(0x0112a91a, "Developer", "Discard battle results", "HP, MP and AP from the battle aren't kept", Risk: Risk.Progress),
        new(0x0112a918, "Developer", "Slow battle loading", "each loading step waits about a second"),
        new(0x0112a902, "Developer", "Every spell plays effect 0", "every command shows the developers' test effect"),
        new(0x0112a903, "Developer", "Event effects use file 0", "scripted effects show the developers' test effect"),
        new(0x0112a917, "Developer", "Test hit effects", "a developer test for hit effects"),
        new(0x0112a91b, "Developer", "Map circle", "shows a test circle when a battle map loads"),
        new(0x0112a907, "Developer", "Invert 'will die'", "enemy AI's 'about to die' check gives the opposite answer"),
        new(0x0112a90b, "Developer", "Print voice info", "writes voice lines to the Log tab"),
        new(0x0112a920, "Developer", "New game: all weapons", "a new game hands out extra equipment"),
    ];

    /// <param name="ReadsDisc">Reads a file synchronously, so it must not run while a battle loads or ends.</param>
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
            values[i] = TOGGLES[i].IsU32 ? Mem.u32(TOGGLES[i].Va) : Mem.u8(TOGGLES[i].Va);
        }
        List<ActorRow> rows = [];
        nint top = *(nint*)Mem.at(G.BtlChrTop);
        sbyte status = Mem.i8(G.BtlStatus);
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
            if (t.IsU32) Mem.set_u32(t.Va, on ? 1u : 0u);
            else Mem.set_u8(t.Va, on ? (byte)1 : (byte)0);
        });
    }

    public void run_cheat(Cheat c) => GameThread.post(c.Label, () => {
        sbyte status = Mem.i8(G.BtlStatus);
        if (c.BattleOnly && status == 0) { GameThread.last_status = $"{c.Label}: only in battle"; return; }
        if (c.ReadsDisc && status is not (0 or 1)) { GameThread.last_status = $"{c.Label}: wait until the battle is running"; return; }
        Calls.void_fn(c.Va).fnptr!();
        GameThread.last_status = $"done: {c.Label}";
    });

    public void weak(bool monsters, bool on) => GameThread.post(monsters ? "monsters HP" : "party HP", () => {
        if (Mem.i8(G.BtlStatus) == 0) { GameThread.last_status = "only in battle"; return; }
        Calls.int_fn(monsters ? MS_DEBUG_WEAK_MONSTERS : MS_DEBUG_WEAK_PARTY).fnptr!(on ? 1 : 0);
        GameThread.last_status = (monsters ? "monsters" : "party") + (on ? " at 1 HP" : " at full HP");
    });

    /// <summary>Writes an actor field (i32 or u8) if the battle is still the same.</summary>
    public void set_actor(int index, int offset, int value, bool is_byte) => GameThread.post("edit actor", () => {
        nint top = *(nint*)Mem.at(G.BtlChrTop);
        if (top == 0 || Mem.i8(G.BtlStatus) == 0) { GameThread.last_status = "the battle is over"; return; }
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
        if (_picker_label == 0) { GameThread.last_status = "look up a battle first"; return; }
        if (Mem.i8(G.BtlStatus) != 0) { GameThread.last_status = "already in a battle"; return; }
        if (!FieldTools.jump_allowed()) { GameThread.last_status = "only while you control the player in the field"; return; }
        Calls.void_fn(TO_ALL_CLEAR_MESSAGE_WINDOW).fnptr!();
        Calls.MsBattleLabelExe.fnptr!(_picker_label, 0, 0);
        GameThread.last_status = Mem.u8(G.BtlExe) == 2 ? "battle requested" : "the game refused the battle";
    });

    private D.VoidFn? _next_todaps_draw;

    [UnmanagedCallConv(CallConvs = [ typeof(CallConvCdecl) ])]
    private void h_todaps_draw() {
        // TODapsMakePkt, which draws the debug text buffer, lost its call; run it before TODapsDraw clears the buffer.
        if (native_status_line && _status_frame != _frame && Mem.i8(G.BtlStatus) != 0) {
            _status_frame = _frame;
            Calls.MsBattleProcess_DebugStatus.fnptr!();
            Calls.TODapsMakePkt.fnptr!();
        }
        _next_todaps_draw ??= Calls.TODapsDraw.chain_from(h_todaps_draw).fnptr!;
        _next_todaps_draw();
    }
}

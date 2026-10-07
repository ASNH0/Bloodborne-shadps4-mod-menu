using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace BBModMenu
{
    // Talks to the running shadPS4 process. shadPS4 runs PS4 code natively, so guest
    // addresses are host addresses: the Bloodborne eboot is always mapped at 0x800000000.
    //
    // Two small hooks are installed into Bloodborne 1.09 (CUSA03173) code:
    //   Hook A - per-frame player update. When IsLocalPlayer() is true it records the local
    //            character and its HP block, and runs any queued level change by calling the
    //            game's own level-up handler (so max HP/stamina/defense are recalculated).
    //   Hook B - SetHp(). When God Mode is on and the target is the local player, the new HP
    //            is replaced with max HP, so damage never lands.
    sealed class GameMemory : IDisposable
    {
        public const long Base = 0x800000000L;

        const long HookA = 0x18F6C16;
        static readonly byte[] HookAOrig = { 0x84, 0xC0, 0x75, 0x11, 0x49, 0x8B, 0x45, 0x00 };
        const long HookALocalReturn = 0x18F6C2B;
        const long HookAOtherReturn = 0x18F6C1E;

        const long HookB = 0x1A0D470;
        static readonly byte[] HookBOrig = { 0x44, 0x8B, 0x47, 0x08, 0x85, 0xF6 };
        const long HookBReturn = 0x1A0D476;

        // Level-up menu event handler: (menu*, event 0x1F58 = "apply", arg*). For that event it
        // copies the pending stats/level from the menu object into PlayerGameData and recalculates
        // every derived stat. The fake menu object only needs the fields listed below.
        const long LevelUpHandler = 0x169A810;
        const int LevelUpApplyEvent = 0x1F58;
        const int Menu_Pending = 0x1C8, Menu_Level = 0x1EC, Menu_Echoes = 0x1F8, MenuSize = 0x220;

        // Statics: GameDataMan (+0x08 -> local PlayerGameData), WorldChrMan (+0x60 -> local PlayerIns)
        const long GameDataMan = 0x553B130;
        const long WorldChrMan = 0x553E878;

        // Zero padding after .text (executable) and after .bss (writable).
        const long Cave = 0x50DA800;
        const int CaveSize = 0x200;
        const long CaveHookB = Cave + 0x180;
        const long Data = 0x56D3E00;
        const int DataSize = 0x60;

        const int D_Magic = 0x00, D_God = 0x08, D_Chr = 0x10, D_Stat = 0x18, D_Pgd = 0x20, D_HitsA = 0x28, D_HitsB = 0x30;
        const int D_Req = 0x40, D_ReqDone = 0x44, D_Busy = 0x48, D_ReqIndex = 0x4C, D_ReqValue = 0x50, D_ReqLevel = 0x54;
        static readonly byte[] Magic = { (byte)'B', (byte)'B', (byte)'M', (byte)'O', (byte)'D', (byte)'v', (byte)'2', 0 };

        // PlayerGameData fields
        public const int Pgd_Insight = 0x84, Pgd_Level = 0x90, Pgd_Echoes = 0x94;
        public const int Stat_Hp = 0xF8, Stat_MaxHp = 0xFC;
        public const int MaxEchoes = 999999999, MaxInsight = 99, MinStat = 1, MaxStat = 99;

        // The level-up menu keeps 9 values; slots 2 and 5 are unused leftovers, slot 8 is insight.
        static readonly int[] SlotOffsets = { 0x40, 0x48, 0x50, 0x58, 0x60, 0x88, 0x68, 0x70, 0x84 };
        public static readonly string[] AttributeNames = { "Vitality", "Endurance", "Strength", "Skill", "Bloodtinge", "Arcane" };
        static readonly int[] AttributeSlots = { 0, 1, 3, 4, 6, 7 };

        public readonly int Pid;
        IntPtr handle;

        public bool Installed { get; private set; }
        public string LastError { get; private set; }

        public GameMemory(int pid)
        {
            Pid = pid;
            handle = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION |
                                        Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SUSPEND_RESUME, false, pid);
            if (handle == IntPtr.Zero)
                throw new AttachException("Cannot open shadPS4 process");
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero) { Native.CloseHandle(handle); handle = IntPtr.Zero; }
        }

        public bool IsAlive
        {
            get
            {
                uint code;
                return handle != IntPtr.Zero && Native.GetExitCodeProcess(handle, out code) && code == Native.STILL_ACTIVE;
            }
        }

        // ---------- raw memory ----------

        public byte[] Read(long addr, int len)
        {
            var buf = new byte[len];
            IntPtr got;
            if (!Native.ReadProcessMemory(handle, new IntPtr(addr), buf, new IntPtr(len), out got) || got.ToInt64() != len)
                return null;
            return buf;
        }

        bool WriteRaw(long addr, byte[] data)
        {
            IntPtr done;
            return Native.WriteProcessMemory(handle, new IntPtr(addr), data, new IntPtr(data.Length), out done) && done.ToInt64() == data.Length;
        }

        bool WriteCode(long addr, byte[] data)
        {
            uint old;
            if (!Native.VirtualProtectEx(handle, new IntPtr(addr), new IntPtr(data.Length), Native.PAGE_EXECUTE_READWRITE, out old))
                return false;
            bool ok = WriteRaw(addr, data);
            uint dummy;
            Native.VirtualProtectEx(handle, new IntPtr(addr), new IntPtr(data.Length), old, out dummy);
            Native.FlushInstructionCache(handle, new IntPtr(addr), new IntPtr(data.Length));
            return ok;
        }

        public long? ReadI64(long addr) { var b = Read(addr, 8); return b == null ? (long?)null : BitConverter.ToInt64(b, 0); }
        public int? ReadI32(long addr) { var b = Read(addr, 4); return b == null ? (int?)null : BitConverter.ToInt32(b, 0); }
        public bool WriteI32(long addr, int v) { return WriteRaw(addr, BitConverter.GetBytes(v)); }

        uint ProtectionOf(long addr)
        {
            Native.MEMORY_BASIC_INFORMATION mbi;
            if (Native.VirtualQueryEx(handle, new IntPtr(addr), out mbi, new IntPtr(Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION)))) == IntPtr.Zero)
                return 0;
            return mbi.State == Native.MEM_COMMIT ? mbi.Protect : 0;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static bool AllZero(byte[] a)
        {
            if (a == null) return false;
            foreach (var x in a) if (x != 0) return false;
            return true;
        }

        // ---------- code generation ----------

        static byte[] BuildCave()
        {
            const long D = Base + Data;
            var a = new Asm(Base + Cave);

            // Hook A: al = IsLocalPlayer(), r13 = character
            a.Bytes(0x84, 0xC0);                                   // test al, al
            int toOther = a.Jcc32(0x84);                           // jz other
            a.Rel(new byte[] { 0x4C, 0x89, 0x2D }, D + D_Chr);     // mov [chr], r13
            a.Bytes(0x49, 0x8B, 0x85, 0xC0, 0x03, 0x00, 0x00);     // mov rax, [r13+0x3C0]  (PlayerGameData)
            a.Rel(new byte[] { 0x48, 0x89, 0x05 }, D + D_Pgd);     // mov [pgd], rax
            a.Bytes(0x49, 0x8B, 0x85, 0xB0, 0x03, 0x00, 0x00);     // mov rax, [r13+0x3B0]
            a.Bytes(0x48, 0x85, 0xC0);                             // test rax, rax
            int toDone = a.Jcc8(0x74);                             // jz done
            a.Bytes(0x48, 0x8B, 0x40, 0x20);                       // mov rax, [rax+0x20]   (HP block owner)
            a.Rel(new byte[] { 0x48, 0x89, 0x05 }, D + D_Stat);    // mov [stat], rax
            a.Bind(toDone);
            a.Rel(new byte[] { 0x48, 0xFF, 0x05 }, D + D_HitsA);   // inc qword [hitsA]
            a.Rel(new byte[] { 0x83, 0x3D }, D + D_Req, 0x00);     // cmp dword [req], 0
            int toReq = a.Jcc8(0x75);                              // jne request
            a.Rel(new byte[] { 0xE9 }, Base + HookALocalReturn);   // jmp back (local path)
            a.Bind32(toOther);
            a.Bytes(0x49, 0x8B, 0x45, 0x00);                       // mov rax, [r13]        (original)
            a.Rel(new byte[] { 0xE9 }, Base + HookAOtherReturn);   // jmp back (other path)

            // Queued level change. Caller-saved registers are dead here (the original code is
            // about to make a call), callee-saved ones are preserved by the handler.
            a.Bind(toReq);
            a.Rel(new byte[] { 0xC7, 0x05 }, D + D_Req, 0, 0, 0, 0);  // mov dword [req], 0
            a.Rel(new byte[] { 0xC7, 0x05 }, D + D_Busy, 1, 0, 0, 0); // mov dword [busy], 1
            a.Bytes(0x55);                                         // push rbp
            a.Bytes(0x48, 0x89, 0xE5);                             // mov rbp, rsp
            a.Bytes(0x48, 0x81, 0xEC, 0x40, 0x02, 0x00, 0x00);     // sub rsp, 0x240
            a.Bytes(0x48, 0x83, 0xE4, 0xF0);                       // and rsp, -16
            a.Bytes(0x48, 0x89, 0xE7);                             // mov rdi, rsp
            a.Bytes(0xB9, 0x48, 0x00, 0x00, 0x00);                 // mov ecx, 0x48        (0x240 / 8)
            a.Bytes(0x31, 0xC0);                                   // xor eax, eax
            a.Bytes(0xF3, 0x48, 0xAB);                             // rep stosq            (arg {0,0} at rsp, menu at rsp+0x20)
            a.Bytes(0x4D, 0x8B, 0x85, 0xC0, 0x03, 0x00, 0x00);     // mov r8, [r13+0x3C0]  (PlayerGameData)
            for (int i = 0; i < SlotOffsets.Length; i++)           // pending[i] = current value
            {
                a.Bytes(0x41, 0x8B, 0x80).Bytes(BitConverter.GetBytes(SlotOffsets[i]));            // mov eax, [r8+off]
                a.Bytes(0x89, 0x84, 0x24).Bytes(BitConverter.GetBytes(0x20 + Menu_Pending + i * 4)); // mov [rsp+menu+..], eax
            }
            a.Bytes(0x41, 0x8B, 0x80).Bytes(BitConverter.GetBytes(Pgd_Echoes));                   // mov eax, [r8+0x94]
            a.Bytes(0x89, 0x84, 0x24).Bytes(BitConverter.GetBytes(0x20 + Menu_Echoes));          // echoes unchanged
            a.Rel(new byte[] { 0x8B, 0x05 }, D + D_ReqIndex);      // mov eax, [reqIndex]
            a.Bytes(0x83, 0xF8, 0x08);                             // cmp eax, 8
            int toBad = a.Jcc8(0x77);                              // ja bad
            a.Rel(new byte[] { 0x8B, 0x0D }, D + D_ReqValue);      // mov ecx, [reqValue]
            a.Bytes(0x89, 0x8C, 0x84).Bytes(BitConverter.GetBytes(0x20 + Menu_Pending)); // mov [rsp+rax*4+pending], ecx
            a.Rel(new byte[] { 0x8B, 0x05 }, D + D_ReqLevel);      // mov eax, [reqLevel]
            a.Bytes(0x89, 0x84, 0x24).Bytes(BitConverter.GetBytes(0x20 + Menu_Level));   // mov [rsp+menu+level], eax
            a.Bytes(0x48, 0x8D, 0x7C, 0x24, 0x20);                 // lea rdi, [rsp+0x20]  (menu)
            a.Bytes(0xBE).Bytes(BitConverter.GetBytes(LevelUpApplyEvent)); // mov esi, 0x1F58
            a.Bytes(0x48, 0x89, 0xE2);                             // mov rdx, rsp         (arg)
            a.Rel(new byte[] { 0xE8 }, Base + LevelUpHandler);     // call level-up handler
            a.Rel(new byte[] { 0xFF, 0x05 }, D + D_ReqDone);       // inc dword [reqDone]
            a.Bind(toBad);
            a.Bytes(0x48, 0x89, 0xEC);                             // mov rsp, rbp
            a.Bytes(0x5D);                                         // pop rbp
            a.Rel(new byte[] { 0xC7, 0x05 }, D + D_Busy, 0, 0, 0, 0); // mov dword [busy], 0
            a.Rel(new byte[] { 0xE9 }, Base + HookALocalReturn);   // jmp back (local path)

            a.PadTo(Base + CaveHookB);
            // Hook B: SetHp(rdi = hp block, esi = new hp). hp at [rdi+8], max hp at [rdi+0xC]
            a.Bytes(0x44, 0x8B, 0x47, 0x08);                       // mov r8d, [rdi+8]      (original)
            a.Rel(new byte[] { 0x80, 0x3D }, D + D_God, 0x00);     // cmp byte [god], 0
            int skip1 = a.Jcc8(0x74);                              // je orig
            a.Bytes(0x50);                                         // push rax
            a.Bytes(0x48, 0x8D, 0x87, 0x10, 0xFF, 0xFF, 0xFF);     // lea rax, [rdi-0xF0]
            a.Rel(new byte[] { 0x48, 0x3B, 0x05 }, D + D_Stat);    // cmp rax, [stat]
            a.Bytes(0x58);                                         // pop rax
            int skip2 = a.Jcc8(0x75);                              // jne orig
            a.Bytes(0x8B, 0x77, 0x0C);                             // mov esi, [rdi+0xC]    (new hp = max hp)
            a.Rel(new byte[] { 0x48, 0xFF, 0x05 }, D + D_HitsB);   // inc qword [hitsB]
            a.Bind(skip1);
            a.Bind(skip2);
            a.Bytes(0x85, 0xF6);                                   // test esi, esi         (original)
            a.Rel(new byte[] { 0xE9 }, Base + HookBReturn);        // jmp back

            var code = a.ToArray();
            if (code.Length > CaveSize) throw new InvalidOperationException("cave overflow");
            var full = new byte[CaveSize];
            Array.Copy(code, full, code.Length);
            return full;
        }

        static byte[] BuildJump(long from, long to, int totalLen)
        {
            var a = new Asm(Base + from);
            a.Rel(new byte[] { 0xE9 }, Base + to);
            while (a.Count < totalLen) a.Bytes(0x90);
            return a.ToArray();
        }

        // A hook written by any version of this tool: jmp into the cave, padded with NOPs.
        static bool IsOurJump(byte[] code, long from)
        {
            if (code == null || code[0] != 0xE9) return false;
            long target = from + 5 + BitConverter.ToInt32(code, 1);
            if (target < Base + Cave || target >= Base + Cave + CaveSize) return false;
            for (int i = 5; i < code.Length; i++) if (code[i] != 0x90) return false;
            return true;
        }

        public static string DumpCave()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var x in BuildCave()) sb.Append(x.ToString("X2")).Append(' ');
            sb.Append("| hookA ");
            foreach (var x in BuildJump(HookA, Cave, HookAOrig.Length)) sb.Append(x.ToString("X2")).Append(' ');
            sb.Append("| hookB ");
            foreach (var x in BuildJump(HookB, CaveHookB, HookBOrig.Length)) sb.Append(x.ToString("X2")).Append(' ');
            return sb.ToString();
        }

        public string DescribeRegions()
        {
            return string.Format("cave prot 0x{0:X} zero={1} | data prot 0x{2:X} zero={3} | hookA prot 0x{4:X}",
                ProtectionOf(Base + Cave), AllZero(Read(Base + Cave, CaveSize)),
                ProtectionOf(Base + Data), AllZero(Read(Base + Data, DataSize)),
                ProtectionOf(Base + HookA));
        }

        // ---------- install ----------

        public enum State { NotLoaded, Ready, Installed, Upgrade, Conflict }

        public State Probe()
        {
            var a = Read(Base + HookA, HookAOrig.Length);
            var b = Read(Base + HookB, HookBOrig.Length);
            var data = Read(Base + Data, 8);
            if (a == null || b == null || data == null) return State.NotLoaded;
            bool ourData = data[0] == 'B' && data[1] == 'B' && data[2] == 'M' && data[3] == 'O' && data[4] == 'D' && data[5] == 'v';
            bool aOrig = Same(a, HookAOrig), bOrig = Same(b, HookBOrig);
            bool aCur = Same(a, BuildJump(HookA, Cave, HookAOrig.Length));
            bool bCur = Same(b, BuildJump(HookB, CaveHookB, HookBOrig.Length));
            if (aCur && bCur && Same(data, Magic) && Same(Read(Base + Cave, CaveSize), BuildCave())) return State.Installed;
            if (aOrig && bOrig) return State.Ready;
            bool aOurs = aOrig || IsOurJump(a, Base + HookA), bOurs = bOrig || IsOurJump(b, Base + HookB);
            if (aOurs && bOurs && ourData) return State.Upgrade;
            return State.Conflict;
        }

        public bool EnsureInstalled()
        {
            LastError = null;
            var st = Probe();
            if (st == State.Installed) { Installed = true; return true; }
            Installed = false;
            if (st == State.NotLoaded) { LastError = "Waiting for Bloodborne to load..."; return false; }
            if (st == State.Conflict) { LastError = "Game code differs from Bloodborne 1.09 (other cheat/patch active?)"; return false; }

            var cave = BuildCave();
            bool upgrade = st == State.Upgrade;
            if (!upgrade)
            {
                if (!AllZero(Read(Base + Cave, CaveSize))) { LastError = "Code cave is not empty - refusing to patch"; return false; }
                if (!AllZero(Read(Base + Data, DataSize))) { LastError = "Data area is not empty - refusing to patch"; return false; }
            }

            uint caveProt = ProtectionOf(Base + Cave);
            if ((caveProt & (Native.PAGE_EXECUTE_READ | Native.PAGE_EXECUTE_READWRITE | Native.PAGE_EXECUTE | Native.PAGE_EXECUTE_WRITECOPY)) == 0)
            { LastError = string.Format("Code cave not executable (0x{0:X})", caveProt); return false; }
            uint dataProt = ProtectionOf(Base + Data);
            if ((dataProt & (Native.PAGE_READWRITE | Native.PAGE_EXECUTE_READWRITE)) == 0)
            { LastError = string.Format("Data area not writable (0x{0:X})", dataProt); return false; }

            var jmpA = BuildJump(HookA, Cave, HookAOrig.Length);
            var jmpB = BuildJump(HookB, CaveHookB, HookBOrig.Length);

            // Freeze the whole process while patching, and make sure no thread is parked in the
            // middle of the bytes being replaced (or inside the old cave when upgrading).
            var busy = new List<long> { Base + HookA + 1, Base + HookA + HookAOrig.Length - 1,
                                        Base + HookB + 1, Base + HookB + HookBOrig.Length - 1 };
            if (upgrade) { busy.Add(Base + Cave); busy.Add(Base + Cave + CaveSize - 1); }
            for (int attempt = 0; attempt < 50; attempt++)
            {
                if (Native.NtSuspendProcess(handle) != 0) { LastError = "Could not suspend shadPS4"; return false; }
                bool clear = false;
                try
                {
                    clear = ThreadsClearOf(busy.ToArray()) && !(upgrade && (ReadI32(Base + Data + D_Busy) ?? 1) != 0);
                    if (clear)
                    {
                        if (!WriteRaw(Base + Data, Magic)) { LastError = "Write failed (data)"; return false; }
                        if (!WriteCode(Base + Cave, cave)) { LastError = "Write failed (cave)"; return false; }
                        if (!WriteCode(Base + HookA, jmpA)) { LastError = "Write failed (hook A)"; return false; }
                        if (!WriteCode(Base + HookB, jmpB)) { LastError = "Write failed (hook B)"; return false; }
                        Installed = true;
                        Log.Write((upgrade ? "hooks upgraded in pid " : "hooks installed into pid ") + Pid);
                        return true;
                    }
                }
                finally
                {
                    Native.NtResumeProcess(handle);
                }
                Thread.Sleep(2);
            }
            LastError = "Game thread busy at hook site, will retry";
            return false;
        }

        // ranges given as pairs [lo, hi] (inclusive)
        bool ThreadsClearOf(long[] ranges)
        {
            Process p;
            try { p = Process.GetProcessById(Pid); } catch { return false; }
            using (p)
            {
                foreach (ProcessThread t in p.Threads)
                {
                    long rip = Native.GetThreadRip(t.Id);
                    for (int i = 0; i < ranges.Length; i += 2)
                        if (rip >= ranges[i] && rip <= ranges[i + 1]) return false;
                }
            }
            return true;
        }

        // ---------- player data ----------

        static bool PlausiblePtr(long p) { return p >= 0x10000 && p < 0x7FFFFFFFFFFFL && (p & 7) == 0; }

        long Deref(long addr) { var p = ReadI64(addr) ?? 0; return PlausiblePtr(p) ? p : 0; }

        public long PlayerData { get { long gdm = Deref(Base + GameDataMan); return gdm == 0 ? 0 : Deref(gdm + 0x08); } }
        public long LocalPlayer { get { long wcm = Deref(Base + WorldChrMan); return wcm == 0 ? 0 : Deref(wcm + 0x60); } }
        public long PlayerStat { get { long chr = LocalPlayer; if (chr == 0) return 0; long m = Deref(chr + 0x3B0); return m == 0 ? 0 : Deref(m + 0x20); } }

        public long HitsA { get { return ReadI64(Base + Data + D_HitsA) ?? 0; } }
        public long HitsB { get { return ReadI64(Base + Data + D_HitsB) ?? 0; } }

        public bool TryGetHp(out int hp, out int maxHp)
        {
            hp = maxHp = 0;
            long st = PlayerStat;
            if (st == 0) return false;
            var b = Read(st + Stat_Hp, 8);
            if (b == null) return false;
            hp = BitConverter.ToInt32(b, 0); maxHp = BitConverter.ToInt32(b, 4);
            return maxHp > 0 && maxHp < 100000 && hp >= 0 && hp <= maxHp;
        }

        // Each frame the game mirrors HP into PlayerGameData (+0x14 hp, +0x18 max). If the mirror
        // agrees with the HP block, both pointers belong to the same live character.
        public bool PlayerDataConsistent()
        {
            long pgd = PlayerData, st = PlayerStat;
            if (pgd == 0 || st == 0) return false;
            var a = Read(pgd + 0x14, 8);
            var b = Read(st + Stat_Hp, 8);
            if (a == null || b == null) return false;
            int pHp = BitConverter.ToInt32(a, 0), pMax = BitConverter.ToInt32(a, 4);
            int hp = BitConverter.ToInt32(b, 0), max = BitConverter.ToInt32(b, 4);
            return max > 0 && max < 100000 && (pHp == hp || pMax == max);
        }

        public sealed class Stats
        {
            public int[] Attr = new int[6];   // in AttributeNames order
            public int Level, Insight, Echoes;
        }

        // Reads level/attributes and checks Bloodborne's rule: level = sum(attributes) - 50.
        public Stats ReadStats()
        {
            long pgd = PlayerData;
            if (pgd == 0 || !PlayerDataConsistent()) return null;
            var blk = Read(pgd, 0x98);
            if (blk == null) return null;
            var s = new Stats();
            int sum = 0;
            for (int i = 0; i < 6; i++)
            {
                s.Attr[i] = BitConverter.ToInt32(blk, SlotOffsets[AttributeSlots[i]]);
                if (s.Attr[i] < MinStat || s.Attr[i] > MaxStat) return null;
                sum += s.Attr[i];
            }
            s.Level = BitConverter.ToInt32(blk, Pgd_Level);
            s.Insight = BitConverter.ToInt32(blk, Pgd_Insight);
            s.Echoes = BitConverter.ToInt32(blk, Pgd_Echoes);
            if (s.Level != sum - 50 || s.Insight < 0 || s.Insight > 999 || s.Echoes < 0 || s.Echoes > MaxEchoes) return null;
            return s;
        }

        public bool AddEchoes(long amount, out int now)
        {
            now = 0;
            var s = ReadStats();
            if (s == null) return false;
            long nv = Math.Max(0, Math.Min(MaxEchoes, s.Echoes + amount));
            if (!WriteI32(PlayerData + Pgd_Echoes, (int)nv)) return false;
            now = (int)nv;
            return true;
        }

        public bool AddInsight(long amount, out int now)
        {
            now = 0;
            var s = ReadStats();
            if (s == null) return false;
            long nv = Math.Max(0, Math.Min(MaxInsight, s.Insight + amount));
            if (!WriteI32(PlayerData + Pgd_Insight, (int)nv)) return false;
            now = (int)nv;
            return true;
        }

        public bool RefillHp()
        {
            int hp, max;
            if (!TryGetHp(out hp, out max)) return false;
            return WriteI32(PlayerStat + Stat_Hp, max);
        }

        public int RequestsDone { get { return ReadI32(Base + Data + D_ReqDone) ?? 0; } }
        public bool RequestPending { get { return (ReadI32(Base + Data + D_Req) ?? 0) != 0; } }

        // Queues "set level to targetLevel by changing one attribute". The game applies it on its
        // next frame through its own level-up code. Returns false (with reason) if not possible.
        public bool RequestLevel(int targetLevel, int attribute, out int newLevel, out int newValue, out string why)
        {
            newLevel = newValue = 0; why = null;
            if (!Installed) { why = "Mod not active yet."; return false; }
            if (RequestPending) { why = "A level change is already waiting - resume the game first."; return false; }
            var s = ReadStats();
            if (s == null) { why = "Character stats not found - resume briefly, then pause again."; return false; }
            int cur = s.Attr[attribute];
            int value = Math.Max(MinStat, Math.Min(MaxStat, cur + (targetLevel - s.Level)));
            int level = s.Level + (value - cur);
            if (level < 1) { value += 1 - level; level = 1; }
            if (value == cur)
            {
                why = targetLevel == s.Level ? "That is already your level."
                    : string.Format("{0} is already at its limit ({1}).", AttributeNames[attribute], cur);
                return false;
            }
            if (!WriteI32(Base + Data + D_ReqIndex, AttributeSlots[attribute]) ||
                !WriteI32(Base + Data + D_ReqValue, value) ||
                !WriteI32(Base + Data + D_ReqLevel, level) ||
                !WriteI32(Base + Data + D_Req, 1))
            { why = "Could not write to the game."; return false; }
            newLevel = level; newValue = value;
            return true;
        }

        // ---------- features ----------

        public bool GodMode
        {
            get { var v = Read(Base + Data + D_God, 1); return v != null && v[0] != 0; }
            set { WriteRaw(Base + Data + D_God, new byte[] { (byte)(value ? 1 : 0) }); }
        }

        // ---------- pause detection ----------

        // shadPS4's pause hotkey suspends every guest thread with SuspendThread().
        public int SuspendedThreadCount()
        {
            int n = 0;
            try
            {
                using (var p = Process.GetProcessById(Pid))
                {
                    foreach (ProcessThread t in p.Threads)
                    {
                        try
                        {
                            if (t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended) n++;
                        }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            catch { }
            return n;
        }
    }

    sealed class AttachException : Exception
    {
        public AttachException(string msg) : base(msg + " (error " + Marshal.GetLastWin32Error() + ")") { }
    }

    // Tiny x86-64 emitter: raw bytes, rel32 operands and forward jumps.
    sealed class Asm
    {
        readonly List<byte> b = new List<byte>();
        readonly long origin;
        public Asm(long origin) { this.origin = origin; }
        public int Count { get { return b.Count; } }
        long Here { get { return origin + b.Count; } }

        public Asm Bytes(params byte[] x) { b.AddRange(x); return this; }

        public Asm Rel(byte[] op, long target, params byte[] trailing)
        {
            b.AddRange(op);
            long rel = target - (Here + 4 + trailing.Length);
            if (rel < int.MinValue || rel > int.MaxValue) throw new InvalidOperationException("rel32 out of range");
            b.AddRange(BitConverter.GetBytes((int)rel));
            b.AddRange(trailing);
            return this;
        }

        public int Jcc8(byte opcode) { b.Add(opcode); b.Add(0); return b.Count - 1; }

        public void Bind(int dispIndex)
        {
            int d = b.Count - (dispIndex + 1);
            if (d > 127) throw new InvalidOperationException("short jump out of range");
            b[dispIndex] = (byte)d;
        }

        // 0F 8x rel32 conditional jump; opcode is the second byte (e.g. 0x84 = jz)
        public int Jcc32(byte opcode) { b.Add(0x0F); b.Add(opcode); b.AddRange(new byte[4]); return b.Count - 4; }

        public void Bind32(int dispIndex)
        {
            var d = BitConverter.GetBytes(b.Count - (dispIndex + 4));
            for (int i = 0; i < 4; i++) b[dispIndex + i] = d[i];
        }

        public void PadTo(long addr)
        {
            if (addr < Here) throw new InvalidOperationException("pad overflow");
            while (Here < addr) b.Add(0xCC);
        }

        public byte[] ToArray() { return b.ToArray(); }
    }
}

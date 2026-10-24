using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace BydjNightreign
{
    // Multi-actor controller: finds every loaded player/monster/NPC through the
    // WorldChrMan ChrSet holders, keeps each character's original scale and
    // applies the jelly curve to all of them every frame.
    internal sealed class GameMemory : IDisposable
    {
        private const uint Access = 0x8 | 0x10 | 0x20 | 0x400;
        private const ulong PlayerOffset = 0x174E8;
        private const ulong CtrlOffset = 0x60;
        private const ulong ScaleOffset = 0x2E4;
        private const ulong HolderCountOffset = 0x16438;
        private const ulong HolderArrayOffset = 0x16440;
        private const int MaxHolders = 200;
        private const int MaxSetCapacity = 6000;
        private const int MaxDropped = 256;
        private const float MinBase = 0.2f;
        private const float MaxBase = 3.0f;

        [DllImport("ntdll.dll")]
        private static extern int NtWriteVirtualMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

        private sealed class Actor
        {
            public ulong Chr;
            public ulong Ctrl;
            public ulong Vtable;
            public ulong Entry;
            public float BaseX;
            public float BaseY;
            public float BaseZ;
            public string ClassName;
        }

        private readonly object sync = new object();
        private readonly List<Actor> actors = new List<Actor>();
        private readonly Dictionary<ulong, Actor> actorsByChr = new Dictionary<ulong, Actor>();
        private readonly List<Actor> dropped = new List<Actor>();
        private readonly Dictionary<ulong, string> rttiCache = new Dictionary<ulong, string>();
        private readonly List<ulong> setList = new List<ulong>();
        private readonly List<int> setCapacity = new List<int>();

        private IntPtr hProc = IntPtr.Zero;
        private Process proc;
        private ulong moduleBase;
        private int moduleSize;
        private ulong slotAddr;
        private ulong manAddr;
        private ulong localPlayer;
        private ulong localCtrl;
        private bool ready;
        private bool localFound;
        private string lastError;
        private string launchSource;
        private string diagnostics = "";

        public string LastError { get { lock (sync) { return lastError; } } }
        public string Diagnostics { get { lock (sync) { return diagnostics; } } }
        public bool Ready { get { lock (sync) { return ready; } } }
        public string LaunchSource { get { lock (sync) { return launchSource; } } }
        public int ActorCount { get { lock (sync) { return actors.Count; } } }
        public bool HasExited
        {
            get
            {
                lock (sync)
                {
                    if (proc == null) return true;
                    try { return proc.HasExited; }
                    catch { return true; }
                }
            }
        }
        public int Pid
        {
            get { lock (sync) { return proc == null ? 0 : proc.Id; } }
        }

        public bool Connect()
        {
            Process p = null;
            try
            {
                Process[] list = Process.GetProcessesByName("nightreign");
                if (list.Length == 0)
                {
                    lastError = "未找到 nightreign.exe，请先离线启动游戏";
                    return false;
                }
                p = list[0];
                for (int i = 1; i < list.Length; i++)
                {
                    try { if (list[i].MainWindowHandle != IntPtr.Zero) { p = list[i]; break; } } catch { }
                }
            }
            catch (Exception ex)
            {
                lastError = "查找游戏进程失败：" + ex.Message;
                return false;
            }

            string launchWhy;
            if (!CheckLaunch(p, out launchWhy))
            {
                lastError = "已拒绝操作：" + launchWhy;
                try { p.Dispose(); } catch { }
                return false;
            }

            IntPtr h = Native.OpenProcess(Access, false, p.Id);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                lastError = "无法打开游戏进程（错误 " + err + "）。EAC 在线模式会阻止访问，请离线启动。";
                try { p.Dispose(); } catch { }
                return false;
            }

            ProcessModule mod;
            try { mod = p.MainModule; }
            catch (Exception ex)
            {
                Native.CloseHandle(h);
                try { p.Dispose(); } catch { }
                lastError = "无法读取游戏模块：" + ex.Message;
                return false;
            }

            lock (sync)
            {
                hProc = h;
                proc = p;
                moduleBase = (ulong)mod.BaseAddress.ToInt64();
                moduleSize = mod.ModuleMemorySize;
                slotAddr = 0;
                manAddr = 0;
                localPlayer = 0;
                localCtrl = 0;
                ready = false;
                lastError = null;
                clothFixApplied = false;
                clothFixAddr = 0;
                clothFixBackup = null;
                launchSource = launchWhy;
                actors.Clear();
                actorsByChr.Clear();
                dropped.Clear();
                rttiCache.Clear();
            }

            if (!ScanAob())
            {
                lock (sync)
                {
                    Native.CloseHandle(hProc);
                    hProc = IntPtr.Zero;
                    try { proc.Dispose(); } catch { }
                    proc = null;
                    lastError = "未找到 WorldChrMan 特征码，游戏版本可能不匹配";
                }
                return false;
            }

            Refresh();
            return true;
        }

        private static bool CheckLaunch(Process p, out string note)
        {
            note = null;
            try
            {
                foreach (ProcessModule m in p.Modules)
                {
                    string n = m.ModuleName.ToLowerInvariant();
                    if (n.Contains("easyanticheat") || n.Contains("easy_anti_cheat") || n.StartsWith("eac_"))
                    {
                        note = "检测到 Easy Anti-Cheat（" + m.ModuleName + "），请只用 ME3 离线模式。";
                        return false;
                    }
                }
            }
            catch (Exception)
            {
                note = "无法枚举游戏模块，可能受反作弊保护，请只用 ME3 离线模式。";
                return false;
            }

            Dictionary<int, Native.ProcessEntry32> tree = SnapshotProcesses();
            if (tree.Count == 0)
            {
                note = "无法读取进程树，不能确认是否由 ME3 离线启动。";
                return false;
            }

            int pid = p.Id;
            bool me3 = false;
            StringBuilder chain = new StringBuilder();
            for (int depth = 0; depth < 12; depth++)
            {
                Native.ProcessEntry32 e;
                if (!tree.TryGetValue(pid, out e)) break;
                string name = (e.szExeFile ?? "").ToLowerInvariant();
                if (chain.Length > 0) chain.Append(" <- ");
                chain.Append(e.szExeFile);
                if (name.Contains("easyanticheat") || name.Contains("start_protected_game"))
                {
                    note = "检测到 EAC 启动链（" + chain + "），请只用 ME3 离线模式。";
                    return false;
                }
                if (name == "me3.exe" || name == "me3-launcher.exe") me3 = true;
                uint parent = e.th32ParentProcessID;
                if (parent == 0 || parent == (uint)pid) break;
                pid = (int)parent;
            }

            if (!me3)
            {
                note = "游戏不是通过 ME3 启动的（" + chain + "），请使用随附的离线启动器。";
                return false;
            }
            note = "ME3 离线校验通过";
            return true;
        }

        private static Dictionary<int, Native.ProcessEntry32> SnapshotProcesses()
        {
            Dictionary<int, Native.ProcessEntry32> map = new Dictionary<int, Native.ProcessEntry32>();
            IntPtr snapshot = Native.CreateToolhelp32Snapshot(0x2, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
            try
            {
                Native.ProcessEntry32 e = new Native.ProcessEntry32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(Native.ProcessEntry32));
                if (Native.Process32First(snapshot, ref e))
                {
                    do
                    {
                        map[(int)e.th32ProcessID] = e;
                        e.dwSize = (uint)Marshal.SizeOf(typeof(Native.ProcessEntry32));
                    }
                    while (Native.Process32Next(snapshot, ref e));
                }
            }
            catch { }
            finally
            {
                Native.CloseHandle(snapshot);
            }
            return map;
        }

        private bool ScanAob()
        {
            const int Chunk = 16 * 1024 * 1024;
            byte[] buf = new byte[Chunk];
            long off = 0;
            while (off < moduleSize)
            {
                int len = (int)Math.Min((long)Chunk, (long)moduleSize - off);
                IntPtr read;
                bool ok = Native.ReadProcessMemory(hProc, (IntPtr)((long)moduleBase + off), buf, len, out read);
                int got = ok ? (int)read.ToInt64() : 0;
                if (got >= 13)
                {
                    for (int i = 0; i <= got - 13; i++)
                    {
                        if (buf[i] != 0x48 || buf[i + 1] != 0x8B || buf[i + 2] != 0x05) continue;
                        if (buf[i + 7] != 0x0F || buf[i + 8] != 0x28 || buf[i + 9] != 0xF1 ||
                            buf[i + 10] != 0x48 || buf[i + 11] != 0x85 || buf[i + 12] != 0xC0) continue;
                        long rva = off + i;
                        int disp = BitConverter.ToInt32(buf, i + 3);
                        lock (sync) { slotAddr = (ulong)((long)moduleBase + rva + 7 + disp); }
                        return true;
                    }
                }
                if (off + len >= moduleSize) break;
                off += len - 12;
            }
            return false;
        }

        public bool Refresh()
        {
            lock (sync)
            {
                if (hProc == IntPtr.Zero) return false;
                bool ok = ResolveLocked();
                ready = ok;
                return ok;
            }
        }

        // Enumerates every loaded actor, captures bases for new ones and keeps
        // the base of characters that were already tracked.
        public bool CaptureBase()
        {
            lock (sync)
            {
                if (hProc == IntPtr.Zero) return false;
                if (!ResolveManLocked()) { ready = false; return false; }
                dropped.Clear();
                EnumerateLocked();
                ready = localFound && actors.Count > 0;
                return ready;
            }
        }

        // Periodic refresh while bouncing. New actors spawn, old ones despawn.
        public bool RefreshActors()
        {
            lock (sync)
            {
                if (hProc == IntPtr.Zero) return false;
                if (!ResolveManLocked()) { ready = false; return false; }
                EnumerateLocked();
                ready = localFound && actors.Count > 0;
                return ready;
            }
        }

        public bool ApplyJelly(float maxStretch, float poisson, double phase)
        {
            lock (sync)
            {
                if (hProc == IntPtr.Zero) return false;
                if (!ResolveManLocked()) { ready = false; return false; }
                double s = Math.Sin(phase);
                float y = ClampF((float)(1.0 + maxStretch * s));
                float xz = ClampF((float)(1.0 - maxStretch * poisson * s));
                int okCount = 0;
                for (int i = 0; i < actors.Count; i++)
                {
                    Actor a = actors[i];
                    if (!StillValid(a)) continue;
                    if (WriteScale(a.Ctrl, a.BaseX * xz, a.BaseY * y, a.BaseZ * xz)) okCount++;
                }
                if (okCount == 0)
                {
                    ready = false;
                    return false;
                }
                ready = localFound;
                return ready;
            }
        }

        public void Restore()
        {
            lock (sync)
            {
                if (hProc == IntPtr.Zero) return;
                RestoreClothFixLocked();
                for (int i = 0; i < actors.Count; i++)
                {
                    Actor a = actors[i];
                    if (StillValid(a)) WriteScale(a.Ctrl, a.BaseX, a.BaseY, a.BaseZ);
                }
                for (int i = 0; i < dropped.Count; i++)
                {
                    Actor a = dropped[i];
                    if (StillValid(a)) WriteScale(a.Ctrl, a.BaseX, a.BaseY, a.BaseZ);
                }
                dropped.Clear();
            }
        }

        // The cloth blend loop builds a bone every frame and copies it into the pose
        // through the helper at RVA 0x154FA0 (call at 0x2847E63), after the generic
        // bone conversion pass. The blended scale is 1/bodyScale, which cancels the
        // bounce and leaves capes and skirts at 1x. Redirecting that single call to
        // a cave that keeps T/Q but writes S=(1,1,1,0) makes cloth follow the body.
        private static readonly byte[] ClothCallOriginal = { 0xE8, 0x38, 0xD1, 0x90, 0xFD };
        private static readonly byte[] ClothCallSignature = {
            0x48,0x8D,0x0C,0x7F, 0x48,0xC1,0xE1,0x04, 0x48,0x03,0x4B,0x18,
            0xE8,0x38,0xD1,0x90,0xFD
        };
        private const ulong ClothCallRva = 0x2847E63;
        private const int ClothCaveLen = 64;
        private bool clothFixApplied;
        private ulong clothFixAddr;
        private byte[] clothFixBackup;
        private ulong clothCaveAddr;
        private byte[] clothCaveBackup;

        public bool ClothFixApplied { get { lock (sync) { return clothFixApplied; } } }

        public bool ApplyClothFix()
        {
            lock (sync)
            {
                if (hProc == IntPtr.Zero) return false;
                if (clothFixApplied) return true;
                ulong addr = FindClothCallLocked();
                if (addr == 0)
                {
                    lastError = "布料随体型：未找到代码特征（游戏版本可能不匹配）";
                    return false;
                }
                ulong cave = FindClothCaveLocked();
                if (cave == 0)
                {
                    lastError = "布料随体型：未找到可用代码洞";
                    return false;
                }
                if (!WriteCodeLocked(cave, BuildClothCave(), out clothCaveBackup))
                {
                    lastError = "布料随体型：代码洞写入失败";
                    return false;
                }
                long rel = (long)cave - (long)(addr + 5);
                byte[] patch = {
                    0xE8, (byte)(rel & 0xFF), (byte)((rel >> 8) & 0xFF),
                    (byte)((rel >> 16) & 0xFF), (byte)((rel >> 24) & 0xFF)
                };
                List<IntPtr> handles = SuspendThreadsLocked();
                bool ok = WriteCodeLocked(addr, patch, out clothFixBackup);
                ResumeThreads(handles);
                if (!ok)
                {
                    lastError = "布料随体型：代码补丁写入失败";
                    return false;
                }
                clothFixAddr = addr;
                clothCaveAddr = cave;
                clothFixApplied = true;
                return true;
            }
        }

        public void RestoreClothFix()
        {
            lock (sync) { RestoreClothFixLocked(); }
        }

        private void RestoreClothFixLocked()
        {
            if (!clothFixApplied || clothFixAddr == 0) return;
            byte[] backup = clothFixBackup;
            byte[] ignored;
            List<IntPtr> handles = SuspendThreadsLocked();
            bool ok = WriteCodeLocked(clothFixAddr, backup ?? ClothCallOriginal, out ignored);
            ResumeThreads(handles);
            if (!ok) return;
            if (clothCaveAddr != 0 && clothCaveBackup != null)
            {
                byte[] caveIgnored;
                WriteCodeLocked(clothCaveAddr, clothCaveBackup, out caveIgnored);
            }
            clothFixApplied = false;
            clothFixAddr = 0;
            clothFixBackup = null;
            clothCaveAddr = 0;
            clothCaveBackup = null;
        }

        private ulong FindClothCallLocked()
        {
            ulong end = moduleBase + (ulong)moduleSize;
            if (moduleSize <= 0) return 0;
            ulong direct = moduleBase + ClothCallRva;
            if (direct + 5 < end)
            {
                byte[] cur = ReadBytes(direct, 5);
                if (cur != null && ByteEq(cur, ClothCallOriginal)) return direct;
            }
            const int chunk = 4 << 20;
            byte[] buf = new byte[chunk + ClothCallSignature.Length];
            for (ulong off = 0; off < (ulong)moduleSize; off += (ulong)chunk)
            {
                int len = (int)Math.Min((ulong)chunk, (ulong)moduleSize - off);
                byte[] part = ReadBytes(moduleBase + off, len);
                if (part == null) continue;
                Buffer.BlockCopy(part, 0, buf, 0, len);
                int limit = len - ClothCallSignature.Length;
                for (int i = 0; i <= limit; i++)
                {
                    bool match = true;
                    for (int j = 0; j < ClothCallSignature.Length; j++)
                    {
                        if (buf[i + j] != ClothCallSignature[j]) { match = false; break; }
                    }
                    if (match) return moduleBase + off + (ulong)i + 12;
                }
            }
            return 0;
        }

        private ulong FindClothCaveLocked()
        {
            const int chunk = 4 << 20;
            for (ulong off = 0; off + (ulong)ClothCaveLen < (ulong)moduleSize; off += (ulong)chunk)
            {
                int len = (int)Math.Min((ulong)chunk, (ulong)moduleSize - off);
                byte[] part = ReadBytes(moduleBase + off, len);
                if (part == null) continue;
                int run = 0;
                for (int i = 0; i < len; i++)
                {
                    if (part[i] == 0xCC)
                    {
                        run++;
                        if (run >= ClothCaveLen) return moduleBase + off + (ulong)(i - ClothCaveLen + 1);
                    }
                    else run = 0;
                }
            }
            return 0;
        }

        // movups xmm0,[rdx] / movaps [rcx],xmm0        T
        // movups xmm0,[rdx+10] / movaps [rcx+10],xmm0  Q
        // movups xmm0,[rdx+20] / movups xmm1,[rip+9]   S, one
        // movaps xmm0,xmm1 / movaps [rcx+20],xmm0 / ret
        private static byte[] BuildClothCave()
        {
            byte[] b = new byte[50];
            byte[] head = {
                0x0F,0x28,0x02, 0x0F,0x29,0x01,
                0x0F,0x28,0x42,0x10, 0x0F,0x29,0x41,0x10,
                0x0F,0x28,0x42,0x20, 0x0F,0x10,0x0D, 0x09,0x00,0x00,0x00,
                0x0F,0x28,0xC1, 0x0F,0x29,0x41,0x20, 0xC3, 0x90
            };
            Buffer.BlockCopy(head, 0, b, 0, head.Length);
            int o = 34;
            byte[] one = BitConverter.GetBytes(1.0f);
            for (int i = 0; i < 3; i++)
            {
                b[o++] = one[0]; b[o++] = one[1]; b[o++] = one[2]; b[o++] = one[3];
            }
            b[o++] = 0; b[o++] = 0; b[o++] = 0; b[o++] = 0;
            return b;
        }

        private List<IntPtr> SuspendThreadsLocked()
        {
            List<IntPtr> handles = new List<IntPtr>();
            try
            {
                foreach (ProcessThread t in proc.Threads)
                {
                    IntPtr h = Native.OpenThread(0x0002, false, (uint)t.Id);
                    if (h == IntPtr.Zero) continue;
                    if (Native.SuspendThread(h) == uint.MaxValue) Native.CloseHandle(h);
                    else handles.Add(h);
                }
            }
            catch { }
            return handles;
        }

        private static void ResumeThreads(List<IntPtr> handles)
        {
            for (int i = 0; i < handles.Count; i++)
            {
                try { Native.ResumeThread(handles[i]); } catch { }
                Native.CloseHandle(handles[i]);
            }
        }

        private static bool ByteEq(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private bool WriteCodeLocked(ulong addr, byte[] data, out byte[] backup)
        {
            backup = null;
            if (data == null || data.Length == 0) return false;
            byte[] old = ReadBytes(addr, data.Length);
            if (old == null) return false;
            uint oldProt;
            if (!Native.VirtualProtectEx(hProc, (IntPtr)(long)addr, (IntPtr)data.Length, 0x40, out oldProt))
            {
                oldProt = 0;
            }
            IntPtr written = IntPtr.Zero;
            int nt;
            try { nt = NtWriteVirtualMemory(hProc, (IntPtr)(long)addr, data, (IntPtr)data.Length, out written); }
            catch { nt = -1; }
            bool ok = nt == 0 && written.ToInt64() == data.Length;
            if (!ok)
            {
                IntPtr w2;
                ok = Native.WriteProcessMemory(hProc, (IntPtr)(long)addr, data, data.Length, out w2)
                     && w2.ToInt64() == data.Length;
            }
            if (oldProt != 0) Native.VirtualProtectEx(hProc, (IntPtr)(long)addr, (IntPtr)data.Length, oldProt, out oldProt);
            byte[] check = ReadBytes(addr, data.Length);
            if (!ok || check == null || !ByteEq(check, data)) return false;
            backup = old;
            return true;
        }

        private bool ResolveManLocked()
        {
            ulong man = ReadU64(slotAddr);
            if (man < 0x10000)
            {
                diagnostics = string.Format("slot=0x{0:X} man=0x{1:X}", slotAddr, man);
                return false;
            }
            ulong player = ReadU64(man + PlayerOffset);
            if (player < 0x10000)
            {
                diagnostics = string.Format("man=0x{0:X} player=0x{1:X}", man, player);
                return false;
            }
            ulong ctrl = ReadU64(player + CtrlOffset);
            if (ctrl < 0x10000)
            {
                diagnostics = string.Format("player=0x{0:X} ctrl=0x{1:X}", player, ctrl);
                return false;
            }
            ulong vt = ReadU64(ctrl);
            string cn = ClassNameOf(vt);
            if (cn == null || cn.IndexOf("Ctrl", StringComparison.Ordinal) < 0)
            {
                diagnostics = string.Format("ctrl=0x{0:X} vt=0x{1:X} cls={2}", ctrl, vt, cn ?? "null");
                return false;
            }

            manAddr = man;
            localPlayer = player;
            localCtrl = ctrl;
            diagnostics = string.Format("man=0x{0:X} ctrl=0x{1:X} {2}", man, ctrl, cn);
            return true;
        }

        private bool ResolveLocked()
        {
            return ResolveManLocked();
        }

        private void EnumerateLocked()
        {
            if (manAddr == 0) return;
            setList.Clear();
            setCapacity.Clear();
            uint holderCount = ReadU32(manAddr + HolderCountOffset);
            if (holderCount > MaxHolders) holderCount = 0;
            for (uint i = 0; i < holderCount; i++)
            {
                ulong set = ReadU64(manAddr + HolderArrayOffset + i * 0x18UL);
                AddSet(set);
            }

            List<Actor> fresh = new List<Actor>();
            HashSet<ulong> seen = new HashSet<ulong>();
            localFound = false;
            for (int si = 0; si < setList.Count; si++)
            {
                ulong entries = ReadU64(setList[si] + 0x18);
                int cap = setCapacity[si];
                byte[] block = ReadBytes(entries, cap * 0x10);
                if (block == null) continue;
                for (int k = 0; k < cap; k++)
                {
                    int o = k * 0x10;
                    ulong chr = BitConverter.ToUInt64(block, o);
                    if (chr < 0x10000 || seen.Contains(chr)) continue;
                    byte load = block[o + 8], upd = block[o + 9];
                    if (chr != localPlayer && (load != 4 || upd != 4)) continue;

                    string cls = RttiName(chr);
                    if (cls != "PlayerIns" && cls != "EnemyIns" && cls != "ChrIns") continue;
                    ulong ctrl = ReadU64(chr + CtrlOffset);
                    if (ctrl < 0x10000) continue;
                    ulong vt = ReadU64(ctrl);
                    string cn = ClassNameOf(vt);
                    if (cn == null || cn.IndexOf("Ctrl", StringComparison.Ordinal) < 0) continue;
                    if (ReadU64(ctrl + 0x10) != chr) continue;

                    Actor old;
                    float bx, by, bz;
                    if (actorsByChr.TryGetValue(chr, out old))
                    {
                        bx = old.BaseX; by = old.BaseY; bz = old.BaseZ;
                    }
                    else
                    {
                        byte[] sc = ReadBytes(ctrl + ScaleOffset, 12);
                        if (sc == null) continue;
                        bx = BitConverter.ToSingle(sc, 0);
                        by = BitConverter.ToSingle(sc, 4);
                        bz = BitConverter.ToSingle(sc, 8);
                    }
                    if (!Sane(bx) || !Sane(by) || !Sane(bz)) continue;

                    Actor a = new Actor();
                    a.Chr = chr;
                    a.Ctrl = ctrl;
                    a.Vtable = vt;
                    a.Entry = entries + (ulong)o;
                    a.BaseX = bx; a.BaseY = by; a.BaseZ = bz;
                    a.ClassName = cls;
                    if (chr == localPlayer) localFound = true;
                    seen.Add(chr);
                    fresh.Add(a);
                }
            }

            // Characters that vanished while animated: remember them so F7 can
            // still put their scale back if the object is still alive.
            for (int i = 0; i < actors.Count; i++)
            {
                Actor a = actors[i];
                if (seen.Contains(a.Chr)) continue;
                bool duplicate = false;
                for (int j = 0; j < dropped.Count; j++)
                {
                    if (dropped[j].Ctrl == a.Ctrl) { duplicate = true; break; }
                }
                if (!duplicate && dropped.Count < MaxDropped)
                {
                    dropped.Add(a);
                    if (dropped.Count >= MaxDropped) dropped.RemoveAt(0);
                }
            }

            actors.Clear();
            actorsByChr.Clear();
            for (int i = 0; i < fresh.Count; i++)
            {
                actors.Add(fresh[i]);
                actorsByChr[fresh[i].Chr] = fresh[i];
            }
        }

        private void AddSet(ulong set)
        {
            if (set < 0x10000 || setList.Contains(set)) return;
            string n = RttiName(set);
            if (n != "ChrSet" && n != "OpenFieldChrSet") return;
            uint cap = ReadU32(set + 0x10);
            ulong entries = ReadU64(set + 0x18);
            if (cap == 0 || cap > MaxSetCapacity || entries < 0x10000) return;
            setList.Add(set);
            setCapacity.Add((int)cap);
        }

        private bool StillValid(Actor a)
        {
            if (ReadU64(a.Ctrl) != a.Vtable) return false;
            if (ReadU64(a.Ctrl + 0x10) != a.Chr) return false;
            if (ReadU64(a.Chr + CtrlOffset) != a.Ctrl) return false;
            return true;
        }

        private bool WriteScale(ulong ctrl, float x, float y, float z)
        {
            byte[] b = new byte[12];
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, b, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, b, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(z), 0, b, 8, 4);
            IntPtr written = IntPtr.Zero;
            int nt;
            try { nt = NtWriteVirtualMemory(hProc, (IntPtr)((long)(ctrl + ScaleOffset)), b, (IntPtr)12, out written); }
            catch { nt = -1; }
            if (nt == 0 && written.ToInt64() == 12) return true;
            if (!WriteScaleFallback(ctrl, b)) return false;
            return true;
        }

        private bool WriteScaleFallback(ulong ctrl, byte[] b)
        {
            IntPtr written;
            bool ok = Native.WriteProcessMemory(hProc, (IntPtr)((long)(ctrl + ScaleOffset)), b, 12, out written)
                      && written.ToInt64() == 12;
            if (!ok) lastError = "写入角色体型失败（游戏可能已退出或 EAC 拦截）";
            return ok;
        }

        private byte[] ReadBytes(ulong addr, int size)
        {
            if (addr < 0x10000) return null;
            byte[] b = new byte[size];
            IntPtr read;
            try
            {
                if (!Native.ReadProcessMemory(hProc, (IntPtr)((long)addr), b, size, out read)) return null;
            }
            catch { return null; }
            if (read.ToInt64() != size) return null;
            return b;
        }

        private ulong ReadU64(ulong addr)
        {
            byte[] b = ReadBytes(addr, 8);
            if (b == null) return 0;
            return BitConverter.ToUInt64(b, 0);
        }

        private uint ReadU32(ulong addr)
        {
            byte[] b = ReadBytes(addr, 4);
            if (b == null) return 0;
            return BitConverter.ToUInt32(b, 0);
        }

        private bool VtableLooksLikeCtrl(ulong vt)
        {
            string n = ClassNameOf(vt);
            return n != null && n.IndexOf("Ctrl", StringComparison.Ordinal) >= 0;
        }

        private bool InModule(ulong addr)
        {
            return addr >= moduleBase && addr < moduleBase + (ulong)moduleSize;
        }

        private string ReadCString(ulong addr, int max)
        {
            byte[] b = ReadBytes(addr, max);
            if (b == null) return null;
            int n = 0;
            while (n < b.Length && b[n] != 0) n++;
            try { return Encoding.ASCII.GetString(b, 0, n); }
            catch { return null; }
        }

        private string ClassNameOf(ulong vt)
        {
            if (vt < 0x10000) return null;
            string cached;
            if (rttiCache.TryGetValue(vt, out cached)) return cached;
            string result = null;
            ulong col = ReadU64(vt - 8);
            if (InModule(col))
            {
                byte[] td = ReadBytes(col + 12, 4);
                if (td != null)
                {
                    int tdRva = BitConverter.ToInt32(td, 0);
                    if (tdRva != 0)
                    {
                        string name = ReadCString(moduleBase + (ulong)(uint)tdRva + 16, 200);
                        if (name != null)
                        {
                            int at = name.IndexOf('@');
                            if (at > 0) name = name.Substring(0, at);
                            if (name.StartsWith(".?AV")) name = name.Substring(4);
                            else if (name.StartsWith(".?AU")) name = name.Substring(4);
                            if (name.Length > 0) result = name;
                        }
                    }
                }
            }
            rttiCache[vt] = result;
            return result;
        }

        private string RttiName(ulong obj)
        {
            if (obj < 0x10000) return null;
            return ClassNameOf(ReadU64(obj));
        }

        private static bool Sane(float v)
        {
            return !float.IsNaN(v) && !float.IsInfinity(v) && v > MinBase && v < MaxBase;
        }

        private static float ClampF(float v)
        {
            if (v < 0.05f) return 0.05f;
            if (v > 8.0f) return 8.0f;
            return v;
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (hProc != IntPtr.Zero)
                {
                    Native.CloseHandle(hProc);
                    hProc = IntPtr.Zero;
                }
                if (proc != null)
                {
                    try { proc.Dispose(); } catch { }
                    proc = null;
                }
                ready = false;
                slotAddr = 0;
                manAddr = 0;
                localPlayer = 0;
                localCtrl = 0;
                actors.Clear();
                actorsByChr.Clear();
                dropped.Clear();
                rttiCache.Clear();
            }
        }
    }
}

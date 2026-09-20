using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace DustlineAssist
{
    // Dustline 单机辅助（只在对 BOT 的离线对局生效）
    //
    // 关键设计：所有功能都先过 Safe() —— 它检查 Game.Instance.Offline。
    // 只有离线对局（打 BOT）才启用；一旦是联机（StartHost / Connect），全部失效。
    [BepInPlugin("dsh.dustline.assist", "Dustline Solo Assist", "0.1.0")]
    public class AssistPlugin : BaseUnityPlugin
    {
        public static AssistPlugin Inst;

        // ---------- 配置项（会存到 BepInEx\config）----------
        public static ConfigEntry<bool> Esp;
        public static ConfigEntry<bool> EspBox;
        public static ConfigEntry<bool> EspLine;
        public static ConfigEntry<bool> EspHealth;
        public static ConfigEntry<bool> EspDist;
        public static ConfigEntry<bool> EspBotTag;
        public static ConfigEntry<bool> Aimbot;
        public static ConfigEntry<float> AimFov;
        public static ConfigEntry<KeyCode> AimKey;
        public static ConfigEntry<float> AimSpeed;
        public static ConfigEntry<bool> AimDrawTarget;
        public static ConfigEntry<bool> AimShowCircle;
        public static ConfigEntry<float> AimHeadOffset;
        public static ConfigEntry<bool> NoRecoil;
        public static ConfigEntry<bool> NoSpread;
        // BulletTrack 已删除（见文件末尾说明）：
        // 子弹方向来自 Command.Yaw/Pitch，而 Command 是结构体，前缀里改不动；
        // ShotDirection 从未被调用，ShotDirections 填的是 16 元弹道模板表。
        // 唯一能改弹道的办法是动视角 —— 那正是用户不要的，所以整条移除。
        public static ConfigEntry<bool> Wallbang;
        public static ConfigEntry<bool> AutoFire;
        public static ConfigEntry<KeyCode> MenuKey;
        public static ConfigEntry<bool> DebugLog;
        public static ConfigEntry<bool> AutoStart;

        // ===== 已停用：子弹追踪 =====
        // 实测结论（有日志证据）：
        //   · ShotDirection()（单方向版本）从未被调用，计数恒为 0
        //   · 真正被调的是 ShotDirections()，传入的是固定 16 元的"弹道模板表"，
        //     改写它不影响子弹实际飞行方向
        //   · 子弹方向实际来自 Command.Yaw/Pitch，而 Command 是【结构体】，
        //     Harmony 前缀拿到的是副本，改了写不回去
        // 唯一可行办法是改视角 —— 但那会导致"一按左键就吸过去"，是明确不要的行为。
        // 因此整条功能移除。下面这个占位符只让旧实验代码能编译，恒为 false。
        private sealed class DisabledFlag { public bool Value = false; }
        private static readonly DisabledFlag BulletTrack = new DisabledFlag();

        private Texture2D _px;
        private bool _menu = true;
        // 窗口要够高：上一版只给了 300，下面的"自瞄/枪械"分组被切掉了，
        // 所以用户根本看不到那几个勾选项。
        private Rect _win = new Rect(40, 40, 360, 660);
        private bool _autoStarted;

        // 反射缓存
        private static Type _tGame;
        private static FieldInfo _fInstance;
        private static FieldInfo _fYaw;
        private static FieldInfo _fPitch;

        void Awake()
        {
            Inst = this;

            _px = new Texture2D(1, 1);
            _px.SetPixel(0, 0, Color.white);
            _px.Apply();

            Esp      = Config.Bind("ESP", "Enable",   true, "ESP 总开关");
            EspBox   = Config.Bind("ESP", "Box",      true, "画方框");
            EspLine  = Config.Bind("ESP", "Line",     true, "从屏幕上方拉汇聚线");
            EspHealth= Config.Bind("ESP", "Health",   true, "显示血量");
            EspDist  = Config.Bind("ESP", "Distance", true, "显示距离");
            EspBotTag= Config.Bind("ESP", "BotTag",   true, "标记 BOT");
            Aimbot   = Config.Bind("Aimbot", "Enable", false, "自瞄总开关");
            AimFov   = Config.Bind("Aimbot", "Fov",    25f,
                "自瞄范围：准心周围这个角度内的敌人才会被锁（屏幕上那个圈的半径就是这个角度）");
            AimKey   = Config.Bind("Aimbot", "HoldKey", KeyCode.Mouse1,
                "按住这个键才自瞄。设为 None 表示一直自瞄（不推荐，会抢鼠标）");
            AimSpeed = Config.Bind("Aimbot", "Speed", 600f,
                "自瞄转速(度/秒)。调小=更柔和、更不抢鼠标");
            AimDrawTarget = Config.Bind("Aimbot", "DrawTarget", true, "把当前锁定的目标画出来");
            AimShowCircle = Config.Bind("Aimbot", "ShowCircle", true,
                "在屏幕上画出自瞄范围圈（只有圈内的敌人才会被锁）");
            AimHeadOffset = Config.Bind("Aimbot", "HeadOffset", 0f,
                "瞄点的竖直偏移(米)。打高了就填负数（-0.1 之类），打低了填正数");
            NoRecoil  = Config.Bind("Gun", "NoRecoil",    false, "无后座：每帧清零后坐力与后坐力累积");
            NoSpread  = Config.Bind("Gun", "NoSpread",    false, "无散射：子弹完全走准心，不散");
            Wallbang  = Config.Bind("Gun", "Wallbang",    false, "穿墙：子弹穿透一切墙体");
            AutoFire  = Config.Bind("Debug", "AutoFire",  false, "自动开火（测试用，会一直扣扳机）");
            MenuKey  = Config.Bind("UI",  "MenuKey",  KeyCode.Insert, "开关设置页面");
            DebugLog = Config.Bind("Debug", "Verbose", false, "输出调试日志");
            AutoStart = Config.Bind("Debug", "AutoStartSolo", false,
                "启动后自动进入单机对局（方便测试，平时关掉）");

            _tGame = FindType("Dustline.Game");
            if (_tGame != null)
            {
                _fInstance = _tGame.GetField("Instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                Log("找到 Dustline.Game, Instance 字段: " + (_fInstance != null));

                _fYaw = _tGame.GetField("yaw", BFlags);
                _fPitch = _tGame.GetField("pitch", BFlags);
                Log("Game.yaw 字段: " + (_fYaw != null) + "   Game.pitch 字段: " + (_fPitch != null));

                ProbeAim();

                _tV3 = FindType("Dustline.Core.V3");
                if (_tV3 != null)
                {
                    _zeroV3 = Activator.CreateInstance(_tV3);   // 结构体默认值 = 全 0
                    Log("V3 零向量已准备: " + (_zeroV3 != null));
                }
            }
            else
            {
                Log("!! 找不到 Dustline.Game 类型");
            }
        }

        // 探测 V3.Aim(yaw, pitch) 的约定：调用几个固定角度，看返回的方向向量。
        // 这样就不用猜"是度数还是弧度、pitch 正负朝哪"。
        private void ProbeAim()
        {
            Type tV3 = FindType("Dustline.Core.V3");
            if (tV3 == null) { Log("找不到 Dustline.Core.V3"); return; }

            MethodInfo mAim = tV3.GetMethod("Aim",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (mAim == null) { Log("找不到 V3.Aim"); return; }

            try
            {
                float[] tests = new float[] { 0f, 90f, 180f, -90f };
                for (int i = 0; i < tests.Length; i++)
                {
                    object[] args = new object[] { tests[i], 0f };
                    object inst = null;
                    if (!mAim.IsStatic)
                    {
                        ConstructorInfo ci = tV3.GetConstructor(Type.EmptyTypes);
                        inst = (ci != null) ? ci.Invoke(null) : Activator.CreateInstance(tV3);
                    }
                    object r = mAim.Invoke(inst, args);
                    Log("V3.Aim(yaw=" + tests[i] + ", pitch=0) = " + VecStr(ToVec(r)));
                }
                // pitch 方向
                for (int i = 0; i < 2; i++)
                {
                    float pv = (i == 0) ? 45f : -45f;
                    object[] args = new object[] { 0f, pv };
                    object inst = null;
                    if (!mAim.IsStatic)
                    {
                        ConstructorInfo ci = tV3.GetConstructor(Type.EmptyTypes);
                        inst = (ci != null) ? ci.Invoke(null) : Activator.CreateInstance(tV3);
                    }
                    object r = mAim.Invoke(inst, args);
                    Log("V3.Aim(yaw=0, pitch=" + pv + ") = " + VecStr(ToVec(r)));
                }
            }
            catch (Exception e)
            {
                Log("Aim 探测失败: " + e.Message);
            }
        }

        private static string VecStr(Vector3 v)
        {
            return string.Format("({0:0.000}, {1:0.000}, {2:0.000})", v.x, v.y, v.z);
        }

        private static Type FindType(string full)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                try
                {
                    Type t = asms[i].GetType(full, false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        // 静态方法（Harmony 的 prefix/postfix）也要能打日志，所以做成静态
        private static void Log(string s)
        {
            if (Inst != null) Inst.Logger.LogInfo(s);
        }

        // ---------- 运行环境检查 ----------
        public static object GameObj()
        {
            if (_fInstance == null) return null;
            try { return _fInstance.GetValue(null); }
            catch { return null; }
        }

        // 安全性：只有离线对局才返回 true
        public static bool Safe()
        {
            object g = GameObj();
            if (g == null) return false;
            try
            {
                PropertyInfo p = g.GetType().GetProperty("Offline",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p == null) return false;
                object v = p.GetValue(g, null);
                return (v is bool) && (bool)v;
            }
            catch { return false; }
        }

        void Update()
        {
            if (Input.GetKeyDown(MenuKey.Value))
            {
                _menu = !_menu;
            }

            LogHookCounts();

            // 测试用：程序化调用 Game.StartOffline()，绕开合成输入不生效的问题
            if (AutoStart.Value && !_autoStarted && Time.time > 10f)
            {
                _autoStarted = true;
                StartSoloMatch();
            }
        }

        private void StartSoloMatch()
        {
            object g = GameObj();
            if (g == null) { Log("AutoStart: Game 实例为空"); return; }
            try
            {
                MethodInfo m = g.GetType().GetMethod("StartOffline",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) { Log("AutoStart: 找不到 StartOffline()"); return; }
                m.Invoke(g, null);
                Log("AutoStart: 已调用 StartOffline()");
            }
            catch (Exception e)
            {
                Log("AutoStart 失败: " + e.Message);
            }
        }

        // LateUpdate：Unity 保证在所有 Update() 之后执行，
        // 所以这里写 yaw/pitch 不会被游戏的鼠标读取逻辑当场覆盖。
        void LateUpdate()
        {
            try { ApplyNoRecoil(); }
            catch (Exception e) { if (DebugLog.Value) Log("无后座异常: " + e.Message); }
            TryInstallFirePatch();
            TryInstallUpdatePatch();
            TryInstallWallbangPatch();
            TryInstallSpreadPatch();
            TryInstallSurfacePatch();
            TryInstallAutoFirePatch();
            TryInstallTraceBulletPatch();
        }

        // ===== 挂钩 Game.Update（前缀）=====
        // 关键：在游戏"读取鼠标输入 → 生成 Command"之前改 yaw/pitch，
        // 这样当前这一帧开出的枪就是朝目标的（子弹追踪因此才真的生效）。
        // 放在 LateUpdate 里就晚了一帧，只影响下一发。
        private static bool _updatePatched;
        private static int _patchAttemptsU;
        private static Harmony _harmony;

        private void TryInstallUpdatePatch()
        {
            if (_updatePatched || _patchAttemptsU > 30 || _tGame == null) return;
            _patchAttemptsU++;

            MethodInfo m = _tGame.GetMethod("Update",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) { Log("找不到 Game.Update"); return; }

            try
            {
                if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");
                MethodInfo pre = typeof(AssistPlugin).GetMethod("UpdatePrefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _harmony.Patch(m, new HarmonyMethod(pre), null);
                _updatePatched = true;
                Log("已挂钩 Game.Update（自瞄/子弹追踪在输入前生效）");
            }
            catch (Exception e)
            {
                Log("Game.Update 挂钩失败: " + e.Message);
            }
        }

        static void UpdatePrefix()
        {
            try
            {
                if (Inst == null) return;
                // 顺序很重要：先把后坐力清零，再瞄。
                // 放在这里（而不是 LateUpdate）是因为：LateUpdate 时游戏
                // 已经读完输入、生成完指令了，那一枪的后坐力照样会生效。
                Inst.ApplyNoRecoil();
                Inst.RunAimbot();
            }
            catch { }
        }

        // ===== 穿墙：挂钩 BulletPenetration.Remaining =====
        // 这个方法算出"子弹穿过这层墙后还剩多少穿透余量"。
        // 让它永远返回一个大值，子弹就不会被墙挡住。
        private static bool _wallbangPatched;
        private static int _patchAttemptsW;

        private void TryInstallWallbangPatch()
        {
            if (_wallbangPatched || _patchAttemptsW > 30) return;
            _patchAttemptsW++;

            Type tPen = FindType("Dustline.Core.BulletPenetration");
            if (tPen == null) return;

            MethodInfo m = tPen.GetMethod("Remaining",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            if (m == null) { Log("穿墙: 找不到 BulletPenetration.Remaining"); return; }

            try
            {
                if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");
                MethodInfo post = typeof(AssistPlugin).GetMethod("RemainingPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _harmony.Patch(m, null, new HarmonyMethod(post));
                _wallbangPatched = true;
                Log("穿墙: 已挂钩 BulletPenetration.Remaining");
            }
            catch (Exception e)
            {
                Log("穿墙: 挂钩失败 " + e.Message);
            }
        }

        static void RemainingPostfix(ref float __result)
        {
            try
            {
                _cntRemaining++;
                if (!Safe()) return;
                _safeRemaining++;
                // 只统计"进入对局之后"的调用，否则会被加载阶段刷掉
                if (DebugLog.Value && _safeRemaining <= 8)
                {
                    Log("穿透计算 原始返回=" + __result.ToString("0.000")
                        + "  墙穿透开关=" + Wallbang.Value);
                }
                if (!Wallbang.Value) return;
                __result = 9999f;   // 永远有穿透余量
            }
            catch { }
        }

        // ===== 无散射：挂钩 SpreadRadius =====
        // SpreadRadius(...) 返回这一发的散布半径。让它返回 0，子弹就完全走准心。
        private static bool _spreadPatched;
        private static int _patchAttemptsS;

        private void TryInstallSpreadPatch()
        {
            if (_spreadPatched || _patchAttemptsS > 40) return;
            _patchAttemptsS++;

            Type t = null;
            string[] owners = new string[] {
                "Dustline.Core.Weapon", "Dustline.Core.Match", "Dustline.Core.Player"
            };
            MethodInfo m = null;
            for (int i = 0; i < owners.Length; i++)
            {
                Type o = FindType(owners[i]);
                if (o == null) continue;
                MethodInfo cand = o.GetMethod("SpreadRadius",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (cand != null) { m = cand; t = o; break; }
            }
            // 兜底：全局搜一遍
            if (m == null)
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int a = 0; a < asms.Length && m == null; a++)
                {
                    Type[] ts;
                    try { ts = asms[a].GetTypes(); } catch { continue; }
                    for (int i = 0; i < ts.Length; i++)
                    {
                        if (ts[i].FullName == null || ts[i].FullName.IndexOf("Dustline") < 0) continue;
                        MethodInfo cand = ts[i].GetMethod("SpreadRadius",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                        if (cand != null) { m = cand; t = ts[i]; break; }
                    }
                }
            }
            if (m == null) { Log("无散射: 找不到 SpreadRadius"); return; }

            try
            {
                if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");
                MethodInfo post = typeof(AssistPlugin).GetMethod("SpreadPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _harmony.Patch(m, null, new HarmonyMethod(post));
                _spreadPatched = true;
                Log("无散射: 已挂钩 " + t.FullName + ".SpreadRadius");
            }
            catch (Exception e)
            {
                Log("无散射: 挂钩失败 " + e.Message);
            }
        }

        static void SpreadPostfix(ref float __result)
        {
            try
            {
                _cntSpread++;
                if (!NoSpread.Value) return;
                if (!Safe()) return;
                __result = 0f;     // 散布半径 = 0 → 子弹完全走准心
            }
            catch { }
        }

        // ===== 穿墙（第二层）：改墙面的穿透系数 =====
        // BallisticSurface.PenetrationModifier 是每种材质"有多难被打穿"。
        // 它是【引用类型上的字段】，所以可以在后置里直接改（不像 Command 是结构体改不了）。
        private static bool _surfPatched;
        private static int _patchAttemptsF;

        private void TryInstallSurfacePatch()
        {
            if (_surfPatched || _patchAttemptsF > 40) return;
            _patchAttemptsF++;

            Type t = FindType("Dustline.Core.BallisticSurface");
            if (t == null) return;

            MethodInfo post = typeof(AssistPlugin).GetMethod("SurfacePostfix",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (post == null) return;

            int ok = 0;
            string[] names = new string[] { "Named", "For" };
            for (int i = 0; i < names.Length; i++)
            {
                MethodInfo m = t.GetMethod(names[i],
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (m == null) continue;
                try
                {
                    if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");
                    _harmony.Patch(m, null, new HarmonyMethod(post));
                    ok++;
                }
                catch { }
            }
            if (ok > 0)
            {
                _surfPatched = true;
                Log("穿墙: 已挂钩 BallisticSurface 的 " + ok + " 个工厂方法");
            }
        }

        static void SurfacePostfix(object __result)
        {
            try
            {
                _cntSurface++;
                if (!Wallbang.Value) return;
                if (!Safe()) return;
                if (__result == null) return;
                FieldInfo f = __result.GetType().GetField("PenetrationModifier", BFlags);
                if (f != null) f.SetValue(__result, 999f);
            }
            catch { }
        }

        // ===== 自动开火（测试用）：强制 Fire 键为按下状态 =====
        // 这样我能自己验证"开火相关"的功能（无后座/无散射/穿墙/子弹追踪），
        // 否则合成输入进不来，没法测。
        private static bool _heldPatched;
        private static int _patchAttemptsH;
        private static object _fireAction;

        // 挂钩被调用的次数 —— 用来确认补丁真的在代码路径里跑到了。
        // 不数一下就不知道"改了没效果"是因为没生效还是因为没被调用。
        private static int _cntSpread, _cntSurface, _cntRemaining, _cntHeld, _safeRemaining;
        private static int _cntTrace;
        private static float _lastHookLog;

        private void LogHookCounts()
        {
            if (!DebugLog.Value) return;
            if (Time.time - _lastHookLog < 3f) return;
            _lastHookLog = Time.time;
            Log("挂钩调用: 子弹方向=" + (_cntShotDir + _cntShotDirs)
                + " 穿透=" + _cntRemaining + " 追迹=" + _cntTrace
                + " 散布=" + _cntSpread + " 表面=" + _cntSurface
                + " | 自瞄=" + (Aimbot.Value ? "开" : "关")

                + " 无后座=" + (NoRecoil.Value ? "开" : "关")
                + " 无散射=" + (NoSpread.Value ? "开" : "关")
                + " 穿墙=" + (Wallbang.Value ? "开" : "关"));
        }

        private void TryInstallAutoFirePatch()
        {
            if (_heldPatched || _patchAttemptsH > 40) return;
            _patchAttemptsH++;

            Type t = FindType("Dustline.ControlInput");
            Type tAct = FindType("Dustline.GameAction");
            if (t == null || tAct == null) return;

            MethodInfo m = t.GetMethod("Held",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (m == null) return;

            try { _fireAction = Enum.Parse(tAct, "Fire"); }
            catch { Log("自动开火: 解析 GameAction.Fire 失败"); return; }

            try
            {
                if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");
                MethodInfo post = typeof(AssistPlugin).GetMethod("HeldPostfix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _harmony.Patch(m, null, new HarmonyMethod(post));
                _heldPatched = true;
                Log("自动开火: 已挂钩 ControlInput.Held");
            }
            catch (Exception e)
            {
                Log("自动开火: 挂钩失败 " + e.Message);
            }
        }

        static void HeldPostfix(object __0, ref bool __result)
        {
            try
            {
                _cntHeld++;
                if (!AutoFire.Value) return;
                if (!Safe()) return;
                if (__0 == null || _fireAction == null) return;
                if (__0.ToString() == _fireAction.ToString()) __result = true;
            }
            catch { }
        }

        private bool _aimHasTarget;
        private float _lastAimLog;
        private Vector3 _aimTargetPos;
        private bool _aimTargetOnScreen;
        private Vector2 _aimTargetScreen;

        // 自瞄：锁"离准心最近"的敌人（在 AimFov 角度内），把视角直接写过去。
        // 角度约定来自 ProbeAim() 的实测：
        //   dir = (sin(yaw)cos(pitch), -sin(pitch), cos(yaw)cos(pitch))
        //   反解 yaw = atan2(dx, dz),  pitch = atan2(-dy, hypot(dx,dz))   （度数）
        private void RunAimbot()
        {
            _aimHasTarget = false;

            // 自瞄：必须【按住 AimKey】才生效（默认鼠标右键）。
            // 不按键时完全不碰视角 —— 这是"锁死鼠标"问题的根治办法。
            //
            // 注意：子弹追踪【不在这里】处理了。它改成挂钩 Weapons.ShotDirection
            // 直接改写子弹方向，所以左键开火不会再抢视角（上一版就是把它挂在这，
            // 导致"一按左键就吸到人身上"）。
            bool wantAim = Aimbot.Value &&
                (AimKey.Value == KeyCode.None || Input.GetKey(AimKey.Value));

            if (!wantAim) return;
            if (!Safe()) return;

            object g = GameObj();
            if (g == null || _fYaw == null || _fPitch == null) return;

            Type tg = g.GetType();
            PropertyInfo pState  = tg.GetProperty("State",  BFlags);
            PropertyInfo pCamera = tg.GetProperty("Camera", BFlags);
            if (pState == null || pCamera == null) return;

            object snap = pState.GetValue(g, null);
            if (snap == null) return;
            Camera cam = pCamera.GetValue(g, null) as Camera;
            if (cam == null) return;

            FieldInfo fPlayers = snap.GetType().GetField("Players", BFlags);
            if (fPlayers == null) return;
            Array players = fPlayers.GetValue(snap) as Array;
            if (players == null) return;

            int localId = GetInt(snap, "LocalId");
            int localTeam = -1;
            Vector3 origin = cam.transform.position;
            for (int i = 0; i < players.Length; i++)
            {
                object pp = players.GetValue(i);
                if (pp == null) continue;
                if (GetInt(pp, "Id") == localId)
                {
                    localTeam = GetInt(pp, "Team");
                    // 用"本地玩家眼睛"当原点，而不是相机位置：
                    // 子弹是从眼睛射出的，用相机位置算角度会有偏差
                    origin = GetV3Prop(pp, "Eye");
                    break;
                }
            }

            Vector3 fwd = cam.transform.forward;

            object best = null;
            Vector3 bestEye = Vector3.zero;
            float bestAng = AimFov.Value;   // 只锁范围圈内的敌人

            for (int i = 0; i < players.Length; i++)
            {
                object p = players.GetValue(i);
                if (p == null) continue;
                if (GetInt(p, "Id") == localId) continue;
                if (GetInt(p, "Team") == localTeam) continue;
                object alive = GetProp(p, "Alive");
                if (alive is bool && !(bool)alive) continue;

                // 瞄敌人的头部（Eye），再按配置往下压一点
                Vector3 eye = GetV3Prop(p, "Eye");
                eye.y += AimHeadOffset.Value;

                Vector3 d = eye - origin;
                if (d.sqrMagnitude < 0.01f) continue;
                float ang = Vector3.Angle(fwd, d);
                if (ang < bestAng)
                {
                    bestAng = ang;
                    best = p;
                    bestEye = eye;
                }
            }

            if (best == null) return;

            // 圈校验：把"锁定目标的角度"换算成屏幕距离，和画出来的圈半径对比。
            // 两者应该接近（角度越大屏幕距离越大），这样圈才可信。
            if (DebugLog.Value && Time.time - _lastAimLog > 1f)
            {
                _lastAimLog = Time.time;
                float ang = Vector3.Angle(fwd, bestEye - origin);
                Vector3 spDbg = cam.WorldToScreenPoint(bestEye);
                float sdDbg = Vector2.Distance(
                    new Vector2(spDbg.x, Screen.height - spDbg.y),
                    new Vector2(Screen.width * 0.5f, Screen.height * 0.5f));
                float radDbg = AngleToScreenRadius(cam);
                Log("圈校验: 目标夹角=" + ang.ToString("0.0") + "°  屏幕上距中心="
                    + sdDbg.ToString("0") + "px  画出的圈半径=" + radDbg.ToString("0") + "px");
            }

            Vector3 dir = (bestEye - origin).normalized;
            float tgtYaw   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
            float tgtPitch = Mathf.Atan2(-dir.y, Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z)) * Mathf.Rad2Deg;

            float curYaw = 0f, curPitch = 0f;
            try { curYaw = Convert.ToSingle(_fYaw.GetValue(g)); } catch { }
            try { curPitch = Convert.ToSingle(_fPitch.GetValue(g)); } catch { }

            // 转速限制：按 AimSpeed 度/秒平滑转过去，鼠标还能正常微调
            float maxStep = (AimSpeed.Value > 0f)
                ? AimSpeed.Value * Time.deltaTime
                : float.MaxValue;

            float dYaw   = Mathf.Clamp(Mathf.DeltaAngle(curYaw, tgtYaw), -maxStep, maxStep);
            float dPitch = Mathf.Clamp(Mathf.DeltaAngle(curPitch, tgtPitch), -maxStep, maxStep);

            _fYaw.SetValue(g, curYaw + dYaw);
            _fPitch.SetValue(g, curPitch + dPitch);

            _aimHasTarget = true;
            _aimTargetPos = bestEye;

            Vector3 sp = cam.WorldToScreenPoint(bestEye);
            _aimTargetOnScreen = sp.z > 0.05f;
            if (_aimTargetOnScreen) _aimTargetScreen = new Vector2(sp.x, Screen.height - sp.y);
        }

        void OnGUI()
        {
            if (_px == null) return;

            bool safe = Safe();
            if (safe && Esp.Value)
            {
                try { DrawEsp(); }
                catch (Exception e) { if (DebugLog.Value) Log("ESP 异常: " + e.Message); }
            }
            // 自瞄范围圈：一眼看出"多大范围内才会吸"
            if (safe && AimShowCircle.Value && Aimbot.Value)
            {
                try { DrawFovCircle(); }
                catch { }
            }
            // 自瞄锁定指示器：画在目标头上，方便确认锁定的是谁
            if (safe && _aimHasTarget && _aimTargetOnScreen && AimDrawTarget.Value)
            {
                float x = _aimTargetScreen.x;
                float y = _aimTargetScreen.y;
                Color c = new Color(0.2f, 1f, 0.4f, 0.95f);
                Quad(x - 14f, y - 1.5f, 28f, 3f, c);
                Quad(x - 1.5f, y - 14f, 3f, 28f, c);
                GUI.Label(new Rect(x + 18f, y - 10f, 220f, 20f), "锁定目标");
            }
            if (_menu) DrawMenu(safe);
        }

        // ---------- 设置页面 ----------
        private void DrawMenu(bool safe)
        {
            _win = GUI.Window(0x44534C, _win, MenuBody, "Dustline 单机辅助");
        }

        private void MenuBody(int id)
        {
            bool safe = Safe();

            GUILayout.Label(safe ? "状态：离线对局 ✓ 辅助可用"
                                 : "状态：非离线（联机/菜单）— 辅助已禁用",
                            GUILayout.Height(22));

            GUILayout.Space(6);
            GUILayout.Label("—— 透视 ESP ——");
            Esp.Value       = GUILayout.Toggle(Esp.Value,       " ESP 总开关");
            EspBox.Value    = GUILayout.Toggle(EspBox.Value,    " 画方框");
            EspLine.Value   = GUILayout.Toggle(EspLine.Value,   " 汇聚线");
            EspHealth.Value = GUILayout.Toggle(EspHealth.Value, " 显示血量");
            EspDist.Value   = GUILayout.Toggle(EspDist.Value,   " 显示距离");
            EspBotTag.Value = GUILayout.Toggle(EspBotTag.Value, " 标记 BOT");

            GUILayout.Space(6);
            GUILayout.Label("—— 自瞄 ——");
            Aimbot.Value        = GUILayout.Toggle(Aimbot.Value,        " 自瞄总开关");
            AimDrawTarget.Value = GUILayout.Toggle(AimDrawTarget.Value, " 画出锁定的目标");
            AimShowCircle.Value = GUILayout.Toggle(AimShowCircle.Value, " 显示自瞄范围圈");
            GUILayout.Label(" 按住【" + AimKey.Value + "】才自瞄（圈内才吸）");
            GUILayout.Label(" 范围 " + AimFov.Value.ToString("0") + "°   转速 "
                + AimSpeed.Value.ToString("0") + "°/秒");
            GUILayout.Label(" （以上都能在配置文件里改）");

            GUILayout.Space(6);
            GUILayout.Label("—— 枪械 ——");
            NoRecoil.Value    = GUILayout.Toggle(NoRecoil.Value,    " 无后座");
            NoSpread.Value    = GUILayout.Toggle(NoSpread.Value,    " 无散射（子弹不散）");
            Wallbang.Value    = GUILayout.Toggle(Wallbang.Value,    " 穿墙（多厚的墙都穿）");

            GUILayout.Space(6);
            GUILayout.Label("—— 调试 ——");
            DebugLog.Value = GUILayout.Toggle(DebugLog.Value, " 输出调试日志");
            AutoFire.Value = GUILayout.Toggle(AutoFire.Value, " 自动开火（测试用）");

            GUILayout.Space(6);
            GUILayout.Label("菜单键：" + MenuKey.Value.ToString() + "（在配置文件里改）");

            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        // ---------- 绘图原语 ----------
        private void Quad(float x, float y, float w, float h, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), _px);
            GUI.color = old;
        }

        private void Line(float x1, float y1, float x2, float y2, float w, Color c)
        {
            float dx = x2 - x1;
            float dy = y2 - y1;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return;
            int steps = (int)(len / 2.0f) + 1;
            float sx = dx / steps;
            float sy = dy / steps;
            float hw = w * 0.5f;
            for (int i = 0; i <= steps; i++)
            {
                Quad(x1 + sx * i - hw, y1 + sy * i - hw, w, w, c);
            }
        }

        private void Box(float x, float y, float w, float h, float t, Color c)
        {
            Quad(x - t, y - t, w + t * 2, t, c);          // 上
            Quad(x - t, y + h, w + t * 2, t, c);          // 下
            Quad(x - t, y, t, h, c);                      // 左
            Quad(x + w, y, t, h, c);                      // 右
        }

        // 画一个圆环（用点拼，GUI 没有直接的圆）
        private void Circle(float cx, float cy, float r, float thick, Color c, int segs)
        {
            if (r < 2f || r > 6000f) return;
            for (int i = 0; i < segs; i++)
            {
                float a = (float)i / segs * Mathf.PI * 2f;
                Quad(cx + Mathf.Cos(a) * r - thick * 0.5f,
                     cy + Mathf.Sin(a) * r - thick * 0.5f, thick, thick, c);
            }
        }

        // 把"角度"换算成"屏幕像素半径"，这样圈才和自瞄范围严格对应。
        // Camera.fieldOfView 是竖直 FOV，所以：
        //   焦距 focal = (屏高/2) / tan(竖直FOV/2)
        //   半径 r     = focal * tan(目标角度/2)
        // 把"角度"换算成"屏幕像素半径"。
        //
        // ⚠ 这里踩过一个坑：锁定判定用的是 Vector3.Angle < AimFov，
        // 也就是 AimFov 是【从准心量起的半径角】；但画圈时我错用了 AimFov/2，
        // 结果圈只有实际锁定范围的一半 —— 用户反馈"实际比圈大很多"就是这个。
        // 现在两边统一：都用 AimFov。
        private float AngleToScreenRadius(Camera cam)
        {
            if (cam == null) return 0f;
            float angle = AimFov.Value;
            if (angle <= 0.01f) return 0f;

            Vector3 origin = cam.transform.position;
            // 绕相机右轴转 angle 度 → 得到锥体边缘方向，再投影。
            // 这样和锁定判定完全一致（都不依赖 fieldOfView 的语义）。
            Vector3 edge = Quaternion.AngleAxis(angle, cam.transform.right) * cam.transform.forward;
            Vector3 sp = cam.WorldToScreenPoint(origin + edge * 30f);
            if (sp.z <= 0.01f) return 0f;
            return Mathf.Abs(sp.y - Screen.height * 0.5f);
        }

        // 画自瞄范围圈：圈内 = 会被锁，圈外 = 不理
        private void DrawFovCircle()
        {
            object g = GameObj();
            if (g == null) return;
            PropertyInfo pCam = g.GetType().GetProperty("Camera", BFlags);
            if (pCam == null) return;
            Camera cam = pCam.GetValue(g, null) as Camera;
            if (cam == null) return;

            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            float r = AngleToScreenRadius(cam);

            // 按住键（正在自瞄）时变亮变绿，一眼知道"现在生效中"
            bool holding = Aimbot.Value &&
                (AimKey.Value == KeyCode.None || Input.GetKey(AimKey.Value));
            Color c;
            if (holding && _aimHasTarget) c = new Color(0.25f, 1f, 0.35f, 0.85f);
            else if (holding)            c = new Color(1f, 0.9f, 0.25f, 0.75f);
            else                         c = new Color(0.35f, 0.85f, 1f, 0.40f);

            Circle(cx, cy, r, 2f, c, 180);

            GUI.Label(new Rect(cx + r + 6f, cy - 10f, 300f, 20f),
                "自瞄范围 " + AimFov.Value.ToString("0") + "°（圈内才吸）");

            // 数值校验：把"最近敌人的夹角"和"它在屏幕上的像素距离"跟圈半径对比。
            // 三者的关系对得上，圈才可信。
            if (DebugLog.Value && Time.time - _lastAimLog > 2f)
            {
                _lastAimLog = Time.time;
                Vector3 org, hd;
                if (NearestEnemyInfo(out org, out hd))
                {
                    // 关键：夹角和屏幕位置必须用同一个原点（相机），
                    // 否则"眼睛夹角 + 相机投影"混用会得出假数据。
                    Vector3 camPos = cam.transform.position;
                    Vector3 spn = cam.WorldToScreenPoint(hd);
                    if (spn.z > 0.05f)   // 只统计相机前方的目标
                    {
                        float ang = Vector3.Angle(cam.transform.forward, hd - camPos);
                        float dist = Vector2.Distance(
                            new Vector2(spn.x, Screen.height - spn.y), new Vector2(cx, cy));
                        Log("圈校验: 夹角=" + ang.ToString("0.0") + "°"
                            + "  屏幕距中心=" + dist.ToString("0") + "px"
                            + "  圈半径=" + r.ToString("0") + "px"
                            + "  | 屏幕=" + Screen.width + "x" + Screen.height
                            + " FOV=" + cam.fieldOfView.ToString("0"));
                    }
                }
            }
        }

        // ---------- ESP 主体 ----------
        private void DrawEsp()
        {
            object g = GameObj();
            bool dbg = DebugLog.Value && (Time.time - _lastStatLog > 2f);
            if (g == null)
            {
                if (dbg) { _lastStatLog = Time.time; Log("DrawEsp 退出: Game 实例为空"); }
                return;
            }

            PropertyInfo pState  = g.GetType().GetProperty("State",  BFlags);
            PropertyInfo pLocal  = g.GetType().GetProperty("Local",  BFlags);
            PropertyInfo pCamera = g.GetType().GetProperty("Camera", BFlags);
            if (pState == null || pCamera == null)
            {
                if (dbg) { _lastStatLog = Time.time; Log("DrawEsp 退出: 属性缺失 State=" + (pState != null) + " Camera=" + (pCamera != null)); }
                return;
            }

            object snap = pState.GetValue(g, null);
            if (snap == null)
            {
                if (dbg) { _lastStatLog = Time.time; Log("DrawEsp 退出: State 为 null"); }
                return;
            }

            Camera cam = pCamera.GetValue(g, null) as Camera;
            if (cam == null)
            {
                if (dbg) { _lastStatLog = Time.time; Log("DrawEsp 退出: Camera 为 null"); }
                return;
            }

            object local = pLocal != null ? pLocal.GetValue(g, null) : null;
            int localTeam = local != null ? GetInt(local, "Team") : -1;

            // 注意：Snapshot.Players 是【字段】不是属性（dump 里标的是 FIELD），
            // 用 GetProperty 会拿到 null —— 这里踩过坑。
            FieldInfo fPlayers = snap.GetType().GetField("Players", BFlags);
            if (fPlayers == null)
            {
                if (dbg) { _lastStatLog = Time.time; Log("DrawEsp 退出: Snapshot 没有 Players 字段"); }
                return;
            }
            Array players = fPlayers.GetValue(snap) as Array;
            if (players == null)
            {
                if (dbg) { _lastStatLog = Time.time; Log("DrawEsp 退出: Players 为 null"); }
                return;
            }

            int localId = GetInt(snap, "LocalId");

            // 兜底：拿不到 Game.Local 时，从玩家数组里按 LocalId 找自己，取出队伍。
            // 不做这一步的话 localTeam=-1，队友会被当成敌人一起画框。
            if (localTeam < 0)
            {
                for (int i = 0; i < players.Length; i++)
                {
                    object pp = players.GetValue(i);
                    if (pp == null) continue;
                    if (GetInt(pp, "Id") == localId)
                    {
                        localTeam = GetInt(pp, "Team");
                        break;
                    }
                }
            }

            float sw = Screen.width;
            float sh = Screen.height;
            int nTotal = 0, nEnemy = 0, nOnScreen = 0;
            Color colBox = new Color(1f, 0.25f, 0.25f, 0.95f);
            Color colLine = new Color(1f, 0.35f, 0.35f, 0.55f);
            Color colTxt = new Color(1f, 1f, 1f, 0.95f);

            for (int i = 0; i < players.Length; i++)
            {
                object p = players.GetValue(i);
                if (p == null) continue;
                nTotal++;

                int id = GetInt(p, "Id");
                if (id == localId) continue;                 // 跳过自己

                int team = GetInt(p, "Team");
                if (localTeam >= 0 && team == localTeam) continue;   // 跳过队友

                object aliveObj = GetProp(p, "Alive");
                if (aliveObj is bool && !(bool)aliveObj) continue;
                nEnemy++;

                Vector3 feet = GetV3(p, "Position");
                Vector3 head = GetV3Prop(p, "Eye");

                Vector3 sFeet = cam.WorldToScreenPoint(feet);
                Vector3 sHead = cam.WorldToScreenPoint(head);
                if (sFeet.z <= 0.05f || sHead.z <= 0.05f) continue;   // 在相机背后
                nOnScreen++;

                float x1 = sFeet.x, y1 = Screen.height - sFeet.y;
                float x2 = sHead.x, y2 = Screen.height - sHead.y;

                float top = Mathf.Min(y1, y2);
                float yBot = Mathf.Max(y1, y2);
                float hgt = Mathf.Max(yBot - top, 6f);
                float wid = hgt * 0.45f;
                float cx = (x1 + x2) * 0.5f;
                float left = cx - wid * 0.5f;

                if (EspBox.Value)
                {
                    Box(left, top, wid, hgt, 2f, colBox);
                }

                if (EspLine.Value)
                {
                    // 从屏幕上方中间往下拉到框顶，形成"汇聚"观感
                    Line(sw * 0.5f, 0f, cx, top, 1.5f, colLine);
                }

                if (EspHealth.Value)
                {
                    int hp = GetInt(p, "Health");
                    Quad(left - 8f, top, 4f, hgt, new Color(0f, 0f, 0f, 0.6f));
                    float f = Mathf.Clamp01(hp / 100f);
                    Quad(left - 8f, top + hgt * (1f - f), 4f, hgt * f, new Color(0.3f, 1f, 0.3f, 0.95f));
                }

                string tag = "";
                if (EspDist.Value)
                {
                    float d = Vector3.Distance(feet, cam.transform.position);
                    tag += ((int)(d * 10f) / 10f).ToString("0.0") + "m";
                }
                if (EspBotTag.Value)
                {
                    object botFlag = GetField(p, "Bot");
                    if (botFlag is bool && (bool)botFlag) tag += (tag.Length > 0 ? "  " : "") + "BOT";
                }
                if (tag.Length > 0)
                {
                    GUI.Label(new Rect(left, yBot + 2f, 200f, 18f), tag);
                }
            }

            // 屏上调试：直接看数据链路通不通，不用翻日志
            if (DebugLog.Value)
            {
                GUI.Label(new Rect(420f, 360f, 900f, 22f),
                    "ESP调试  玩家=" + nTotal + "  敌人=" + nEnemy + "  在屏=" + nOnScreen
                    + "  本地ID=" + localId + "  本地team=" + localTeam
                    + "  相机=" + (cam != null ? "有" : "无"));

                // 同时写日志（屏上会被别的窗口挡住，日志不会）
                if (Time.time - _lastStatLog > 2f)
                {
                    _lastStatLog = Time.time;
                    Log("ESP: players=" + nTotal + " enemies=" + nEnemy + " onScreen=" + nOnScreen
                        + " localId=" + localId + " localTeam=" + localTeam);
                }
            }
        }

        private float _lastStatLog;

        // ===== 穿墙（第三层）：直接提升武器的穿透力 =====
        // Weapon.Penetration 是这把枪"能穿多厚"的原始值。
        // 在子弹追迹之前把它拉到很大，就能穿任何厚度的墙。
        private static bool _tracePatched;
        private static int _patchAttemptsT;

        private void TryInstallTraceBulletPatch()
        {
            if (_tracePatched || _patchAttemptsT > 40) return;
            _patchAttemptsT++;

            Type t = FindType("Dustline.Core.Match");
            if (t == null) return;
            MethodInfo m = t.GetMethod("TraceBullet",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
            if (m == null) return;

            try
            {
                if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");
                MethodInfo pre = typeof(AssistPlugin).GetMethod("TraceBulletPrefix",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                _harmony.Patch(m, new HarmonyMethod(pre), null);
                _tracePatched = true;
                Log("穿墙: 已挂钩 Match.TraceBullet（开火前拉满武器穿透力）");
            }
            catch (Exception e)
            {
                Log("穿墙: TraceBullet 挂钩失败 " + e.Message);
            }
        }

        // TraceBullet(Player, Command, V3, Weapon) → __3 是 Weapon
        static void TraceBulletPrefix(object __3)
        {
            try
            {
                _cntTrace++;
                if (!Wallbang.Value) return;
                if (!Safe()) return;
                if (__3 == null) return;
                SetFloat(__3, "Penetration", 999f);   // 穿透力拉满
                SetFloat(__3, "RangeModifier", 1f);   // 距离衰减归零
                SetFloat(__3, "Range", 99999f);       // 射程拉满
            }
            catch { }
        }

        private const BindingFlags BFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // ================= 子弹追踪（正确实现）=================
        // Weapons.ShotDirection(Player, Command, SourceRandom, Int32) 返回"这一发往哪飞"。
        // 改写它的返回值 → 子弹拐弯飞向敌人头部，而玩家视角纹丝不动。
        // 这样左键开火不会抢视角（上一版靠改视角实现，才会"一按左键就吸过去"）。
        //
        // 同时挂 ShotDirections(Player, Command, Int32, V3[])：它填一个数组，
        // 数组是引用类型，改里面的元素一定能写回去，作为主路径。
        private static bool _shotDirPatched;
        private static int _patchAttemptsD;
        private static int _cntShotDir, _cntShotDirs;

        private void TryInstallShotDirPatch()
        {
            if (_shotDirPatched || _patchAttemptsD > 40) return;
            _patchAttemptsD++;

            Type t = FindType("Dustline.Core.Weapons");
            if (t == null) return;

            int ok = 0;
            try
            {
                if (_harmony == null) _harmony = new Harmony("dsh.dustline.assist");

                MethodInfo m1 = t.GetMethod("ShotDirection",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (m1 != null)
                {
                    MethodInfo post = typeof(AssistPlugin).GetMethod("ShotDirPostfix",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _harmony.Patch(m1, null, new HarmonyMethod(post));
                    ok++;
                }

                MethodInfo m2 = t.GetMethod("ShotDirections",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                if (m2 != null)
                {
                    MethodInfo post2 = typeof(AssistPlugin).GetMethod("ShotDirsPostfix",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                    _harmony.Patch(m2, null, new HarmonyMethod(post2));
                    ok++;
                }
            }
            catch (Exception e)
            {
                Log("子弹追踪: 挂钩失败 " + e.Message);
                return;
            }

            if (ok > 0)
            {
                _shotDirPatched = true;
                Log("子弹追踪: 已挂钩 Weapons.ShotDirection/ShotDirections 共 " + ok + " 个");
            }
        }

        // 取"本地玩家眼睛"和"最近的敌人头部"，供诊断/瞄准共用
        private static bool NearestEnemyInfo(out Vector3 origin, out Vector3 head)
        {
            origin = Vector3.zero;
            head = Vector3.zero;

            object g = GameObj();
            if (g == null) return false;
            PropertyInfo pState = g.GetType().GetProperty("State", BFlags);
            if (pState == null) return false;
            object snap = pState.GetValue(g, null);
            if (snap == null) return false;
            FieldInfo fp = snap.GetType().GetField("Players", BFlags);
            if (fp == null) return false;
            Array players = fp.GetValue(snap) as Array;
            if (players == null) return false;

            int lid = GetInt(snap, "LocalId");
            int team = -1;
            bool gotSelf = false;
            for (int i = 0; i < players.Length; i++)
            {
                object p = players.GetValue(i);
                if (p == null) continue;
                if (GetInt(p, "Id") == lid)
                {
                    origin = GetV3Prop(p, "Eye");
                    team = GetInt(p, "Team");
                    gotSelf = true;
                    break;
                }
            }
            if (!gotSelf) return false;

            bool found = false;
            float bd = float.MaxValue;
            for (int i = 0; i < players.Length; i++)
            {
                object p = players.GetValue(i);
                if (p == null) continue;
                if (GetInt(p, "Id") == lid) continue;
                if (GetInt(p, "Team") == team) continue;
                object alive = GetProp(p, "Alive");
                if (alive is bool && !(bool)alive) continue;
                Vector3 h = GetV3Prop(p, "Eye");
                float d = (h - origin).sqrMagnitude;
                if (d < bd) { bd = d; head = h; found = true; }
            }
            return found;
        }

        // 算出"开枪者眼睛 → 最近敌人头部"的单位方向
        private static bool AimDirToNearestEnemy(object shooter, out Vector3 dir)
        {
            dir = Vector3.zero;
            if (shooter == null) return false;

            object g = GameObj();
            if (g == null) return false;
            PropertyInfo pState = g.GetType().GetProperty("State", BFlags);
            if (pState == null) return false;
            object snap = pState.GetValue(g, null);
            if (snap == null) return false;
            FieldInfo fp = snap.GetType().GetField("Players", BFlags);
            if (fp == null) return false;
            Array players = fp.GetValue(snap) as Array;
            if (players == null) return false;

            int sid = GetInt(shooter, "Id");
            int steam = GetInt(shooter, "Team");
            Vector3 eye = GetV3Prop(shooter, "Eye");

            bool found = false;
            float bestD = float.MaxValue;
            Vector3 bestHead = Vector3.zero;

            for (int i = 0; i < players.Length; i++)
            {
                object p = players.GetValue(i);
                if (p == null) continue;
                if (GetInt(p, "Id") == sid) continue;
                if (GetInt(p, "Team") == steam) continue;
                object alive = GetProp(p, "Alive");
                if (alive is bool && !(bool)alive) continue;

                Vector3 head = GetV3Prop(p, "Eye");
                float d = (head - eye).sqrMagnitude;
                if (d < bestD) { bestD = d; bestHead = head; found = true; }
            }
            if (!found) return false;

            Vector3 v = bestHead - eye;
            if (v.sqrMagnitude < 0.0001f) return false;
            dir = v.normalized;
            return true;
        }

        // 改写一个 V3（可能是装箱的结构体）的 X/Y/Z
        private static void AssignV3(object v3, Vector3 v)
        {
            if (v3 == null) return;
            Type t = v3.GetType();
            FieldInfo fx = t.GetField("X", BFlags);
            FieldInfo fy = t.GetField("Y", BFlags);
            FieldInfo fz = t.GetField("Z", BFlags);
            if (fx != null) fx.SetValue(v3, v.x);
            if (fy != null) fy.SetValue(v3, v.y);
            if (fz != null) fz.SetValue(v3, v.z);
        }

        // __0 = Player（开枪者）, __result = V3（方向）
        static void ShotDirPostfix(object __0, ref object __result)
        {
            try
            {
                _cntShotDir++;
                if (!BulletTrack.Value) return;
                if (!Safe()) return;
                Vector3 d;
                if (!AimDirToNearestEnemy(__0, out d)) return;
                // 诊断：打印改写前的原始方向，对比目标方向。
                // 如果下一发的"原"等于上一发的"目标"，说明写回成功了。
                if (DebugLog.Value && _cntShotDir <= 8)
                {
                    Log("子弹方向 改写前=" + VecStr(ToVec(__result)) + "  目标=" + VecStr(d));
                }
                AssignV3(__result, d);
            }
            catch { }
        }

        // __0 = Player, __3 = V3[]（要填的数组）
        static void ShotDirsPostfix(object __0, object __3)
        {
            try
            {
                _cntShotDirs++;
                if (!BulletTrack.Value) return;
                if (!Safe()) return;
                Array arr = __3 as Array;
                if (arr == null || arr.Length == 0) return;
                Vector3 d;
                if (!AimDirToNearestEnemy(__0, out d)) return;
                if (DebugLog.Value && _cntShotDirs <= 8)
                {
                    Log("子弹方向(数组) 长度=" + arr.Length
                        + "  改写前[0]=" + VecStr(ToVec(arr.GetValue(0)))
                        + "  目标=" + VecStr(d));
                }
                for (int i = 0; i < arr.Length; i++)
                {
                    object item = arr.GetValue(i);
                    if (item != null) AssignV3(item, d);
                }
            }
            catch { }
        }

        // ================= 无后座 =================
        // 后坐力分散在几处：Game 上的镜头冲量 + Player 上的模拟后坐力。
        // 每帧全部清零，枪口就不会被推高。
        private static Type _tV3;
        private static object _zeroV3;

        private void ApplyNoRecoil()
        {
            if (!NoRecoil.Value || !Safe()) return;
            object g = GameObj();
            if (g == null || _zeroV3 == null) return;

            ZeroV3(g, "cameraRecoil");      // 镜头冲量
            ZeroV3(g, "priorRecoil");
            ZeroV3(g, "priorViewPunch");

            object local = GetProp(g, "Local");
            if (local != null)
            {
                ZeroV3(local, "Recoil");          // 模拟后坐力
                ZeroV3(local, "RecoilVelocity");
                ZeroV3(local, "ViewPunch");
                // 这两项才是"子弹散射"的元凶：
                //   AccuracyPenalty 是持续开火累积的精度惩罚
                //   RecoilIndex 是当前在连发弹道里的位置（决定散布）
                SetFloat(local, "AccuracyPenalty", 0f);
                SetFloat(local, "RecoilIndex", 0f);
            }
        }

        private static void SetFloat(object o, string name, float v)
        {
            if (o == null) return;
            FieldInfo f = o.GetType().GetField(name, BFlags);
            if (f != null)
            {
                try { f.SetValue(o, v); } catch { }
            }
        }

        private static void ZeroV3(object o, string name)
        {
            if (o == null || _zeroV3 == null) return;
            FieldInfo f = o.GetType().GetField(name, BFlags);
            if (f != null)
            {
                try { f.SetValue(o, _zeroV3); } catch { }
            }
        }

        // ================= 子弹追踪（Harmony 挂钩 Match.Fire）=================
        // 原理：开枪前把 Command 里的 Yaw/Pitch 改写成"指向最近敌人头部"，
        // 于是无论玩家准心朝哪，这一发都会飞向头部。
        private static bool _firePatched;
        private static int _patchAttempts;

        private void TryInstallFirePatch()
        {
            if (_firePatched || _patchAttempts > 30) return;
            _patchAttempts++;

            Type tMatch = FindType("Dustline.Core.Match");
            if (tMatch == null) return;   // 程序集还没加载，下一帧再试

            MethodInfo mFire = tMatch.GetMethod("Fire",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mFire == null) { Log("BulletTrack: 找不到 Match.Fire"); return; }

            MethodInfo prefix = typeof(AssistPlugin).GetMethod("FirePrefix",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (prefix == null) { Log("BulletTrack: 找不到 FirePrefix"); return; }

            try
            {
                Harmony h = new Harmony("dsh.dustline.assist.fire");
                h.Patch(mFire, new HarmonyMethod(prefix), null);
                _firePatched = true;
                Log("BulletTrack: 已挂钩 Match.Fire");

                Type tCmd = FindType("Dustline.Core.Command");
                if (tCmd != null)
                {
                    Log("BulletTrack: Command 是 " + (tCmd.IsValueType ? "值类型(结构体)" : "引用类型(类)"));
                }
            }
            catch (Exception e)
            {
                Log("BulletTrack: 挂载失败 " + e.Message);
            }
        }

        // __instance = Match, __0 = Player(开枪者), __1 = Command
        static void FirePrefix(object __instance, object __0, object __1)
        {
            try
            {
                if (!BulletTrack.Value) return;
                if (!Safe()) return;
                if (__instance == null || __0 == null || __1 == null) return;

                object shooter = __0;
                object cmd = __1;

                FieldInfo fp = __instance.GetType().GetField("Players", BFlags);
                if (fp == null) return;
                Array players = fp.GetValue(__instance) as Array;
                if (players == null) return;

                int shooterId = GetInt(shooter, "Id");
                int shooterTeam = GetInt(shooter, "Team");
                Vector3 eye0 = GetV3Prop(shooter, "Eye");

                object best = null;
                Vector3 bestHead = Vector3.zero;
                float bestD = float.MaxValue;

                for (int i = 0; i < players.Length; i++)
                {
                    object p = players.GetValue(i);
                    if (p == null) continue;
                    if (GetInt(p, "Id") == shooterId) continue;
                    if (GetInt(p, "Team") == shooterTeam) continue;
                    object alive = GetProp(p, "Alive");
                    if (alive is bool && !(bool)alive) continue;

                    Vector3 head = GetV3Prop(p, "Eye");
                    float d = (head - eye0).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = p; bestHead = head; }
                }

                if (best == null) return;

                Vector3 dir = (bestHead - eye0).normalized;
                float yaw   = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(-dir.y, Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z)) * Mathf.Rad2Deg;

                // Command 的 Yaw/Pitch 是字段；若是值类型则这里改不到，日志里会提示
                FieldInfo fy = cmd.GetType().GetField("Yaw", BFlags);
                FieldInfo fpch = cmd.GetType().GetField("Pitch", BFlags);
                if (fy != null) fy.SetValue(cmd, yaw);
                if (fpch != null) fpch.SetValue(cmd, pitch);
            }
            catch { }
        }

        private static object GetProp(object o, string name)
        {
            try
            {
                PropertyInfo p = o.GetType().GetProperty(name, BFlags);
                return p != null ? p.GetValue(o, null) : null;
            }
            catch { return null; }
        }

        private static object GetField(object o, string name)
        {
            try
            {
                FieldInfo f = o.GetType().GetField(name, BFlags);
                return f != null ? f.GetValue(o) : null;
            }
            catch { return null; }
        }

        private static int GetInt(object o, string name)
        {
            object v = GetField(o, name);
            if (v == null) v = GetProp(o, name);
            if (v == null) return 0;
            try { return Convert.ToInt32(v); }
            catch { return 0; }
        }

        private static Vector3 GetV3(object o, string fieldName)
        {
            object v = GetField(o, fieldName);
            return ToVec(v);
        }

        private static Vector3 GetV3Prop(object o, string propName)
        {
            object v = GetProp(o, propName);
            return ToVec(v);
        }

        // Dustline.Core.V3 -> UnityEngine.Vector3
        private static Vector3 ToVec(object v3)
        {
            if (v3 == null) return Vector3.zero;
            Type t = v3.GetType();
            FieldInfo fx = t.GetField("X", BFlags);
            if (fx == null) return Vector3.zero;
            FieldInfo fy = t.GetField("Y", BFlags);
            FieldInfo fz = t.GetField("Z", BFlags);
            return new Vector3(
                Convert.ToSingle(fx.GetValue(v3)),
                Convert.ToSingle(fy.GetValue(v3)),
                Convert.ToSingle(fz.GetValue(v3)));
        }
    }
}

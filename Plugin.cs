using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Apocaunloader
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "com.denis.apocalypter.apocaunloader";
        public const string NAME = "Apocaunloader";
        public const string VERSION = "1.1.1";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<float> HoldSeconds;
        internal static ConfigEntry<Key> FallbackKey;
        internal static ConfigEntry<float> PhaseTimeout;
        internal static ConfigEntry<bool> Verbose;

        // Frame on which a "tap" (short press released) of the Reload button happened.
        internal static int TapFrame = -100;
        private static GameObject _runnerGo;

        private void Awake()
        {
            Log = Logger;
            Config.Bind("General", "Apocasetter", true, "Show this mod in the Apocasetter Mods menu");
            Enabled = Config.Bind("General", "Enabled", true, "Enable hold-to-unload. When disabled the game's reload behaves exactly as before.");
            HoldSeconds = Config.Bind("General", "HoldSeconds", 0.35f, new ConfigDescription("How long the Reload button must be held to unload instead of reload. A shorter press reloads (on release).", new AcceptableValueRange<float>(0.1f, 2f)));
            PhaseTimeout = Config.Bind("General", "AnimationTimeout", 4f, new ConfigDescription("Safety timeout (seconds) if an animation event never arrives.", new AcceptableValueRange<float>(1f, 10f)));
            FallbackKey = Config.Bind("General", "FallbackKey", Key.R, "Key polled if the game's 'Reload' input axis cannot be read.");
            Verbose = Config.Bind("General", "VerboseLog", true, "Log every step to the BepInEx console/log.");

            var harmony = new Harmony(GUID);
            harmony.PatchAll(typeof(Plugin).Assembly);

            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureRunner("Awake");
            Log.LogInfo(NAME + " " + VERSION + " loaded. Hold Reload for " + HoldSeconds.Value + "s to unload the current gun.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { EnsureRunner("sceneLoaded"); }

        internal static void EnsureRunner(string reason)
        {
            if (_runnerGo != null && _runnerGo.activeInHierarchy) return;
            _runnerGo = new GameObject("Apocaunloader.Runner");
            _runnerGo.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(_runnerGo);
            _runnerGo.AddComponent<Runner>();
            if (Verbose.Value) Log.LogInfo("Runner created (" + reason + ")");
        }

        internal static void V(string s) { if (Verbose.Value) Log.LogInfo(s); }

        /// True while the game's Reload button is held (legacy Input axis "Reload", fallback key).
        internal static bool ReloadHeld()
        {
            try { return Input.GetButton("Reload"); } catch { }
            try { var kb = Keyboard.current; if (kb != null) return kb[FallbackKey.Value].isPressed; } catch { }
            try { KeyCode kc; if (Enum.TryParse(FallbackKey.Value.ToString(), out kc)) return Input.GetKey(kc); } catch { }
            return false;
        }

        internal static bool TapPending() { int f = Time.frameCount; return f == TapFrame || f == TapFrame + 1; }
    }

    internal class Runner : MonoBehaviour
    {
        private bool _held;
        private float _pressStart;
        private bool _consumed;
        internal static Unloader Unloader = new Unloader();

        private void Update()
        {
            if (!Plugin.Enabled.Value) { Unloader.Tick(); return; }

            bool down = Plugin.ReloadHeld();
            float now = Time.unscaledTime;

            if (down && !_held)
            {
                _held = true; _pressStart = now; _consumed = false;
            }
            else if (down && _held && !_consumed && now - _pressStart >= Plugin.HoldSeconds.Value)
            {
                _consumed = true;
                if (Time.timeScale > 0f && !Unloader.Busy) Unloader.TryStart();
            }
            else if (!down && _held)
            {
                _held = false;
                if (!_consumed && !Unloader.Busy) { Plugin.TapFrame = Time.frameCount; Plugin.V("Reload tap"); }
            }

            Unloader.Tick();
        }
    }

    /// Drives the unload sequence by replaying the weapon's own Reload FSM events.
    internal class Unloader
    {
        private enum Phase { Idle, WaitAnimFinished, WaitAnimStart, WaitAnimEnd }
        private Phase _phase = Phase.Idle;
        private PlayMakerFSM _reload;
        private GameObject _weapon;
        private PlayMakerFSM _ammoFsm;
        private string _caliber;
        private float _deadline;
        private bool _moved;

        public bool Busy { get { return _phase != Phase.Idle; } }

        public void TryStart()
        {
            var reload = FindActiveReloadFsm();
            if (reload == null) { Plugin.V("No active weapon Reload FSM in idle"); return; }
            var inGun = reload.FsmVariables.GetFsmInt("ammo_in_gun");
            if (inGun == null) { Plugin.V("Reload FSM has no ammo_in_gun"); return; }
            if (inGun.Value <= 0) { Plugin.V("Gun already empty"); return; }

            // Caliber variable name from the checkAmmo state's GetFsmInt action.
            string caliber = null;
            var check = FindState(reload, "checkAmmo");
            if (check != null)
                foreach (var a in check.Actions)
                {
                    var g = a as GetFsmInt;
                    if (g != null && g.fsmName != null && g.fsmName.Value == "Ammo") { caliber = g.variableName.Value; break; }
                }
            if (string.IsNullOrEmpty(caliber)) { Plugin.Log.LogWarning("Could not determine caliber for " + reload.gameObject.name); return; }

            var gm = GameObject.Find("__GameManager__");
            var ammoFsm = gm != null ? PlayMakerFSM.FindFsmOnGameObject(gm, "Ammo") : null;
            if (ammoFsm == null || ammoFsm.FsmVariables.GetFsmInt(caliber) == null) { Plugin.Log.LogWarning("Ammo FSM / variable " + caliber + " not found"); return; }

            // The state the game enters after checkAmmo passes: it sends Deactivate/Reloading/animation events.
            FsmState start = null;
            if (check != null)
                foreach (var t in check.Transitions) if (t.EventName == "next") { start = FindState(reload, t.ToState); break; }
            if (start == null) { Plugin.Log.LogWarning("No start state found for " + reload.gameObject.name); return; }

            _reload = reload; _weapon = reload.gameObject; _ammoFsm = ammoFsm; _caliber = caliber; _moved = false;
            bool threePhase = start.Transitions.Any(t => t.EventName == "AnimStart");
            Plugin.Log.LogInfo("Unloading " + _weapon.name + ": " + inGun.Value + " x " + caliber + (threePhase ? " (3-phase)" : ""));

            ReplaySendEvents(start);
            _phase = threePhase ? Phase.WaitAnimStart : Phase.WaitAnimFinished;
            _deadline = Time.unscaledTime + Plugin.PhaseTimeout.Value;
        }

        /// Called from the Harmony hook on Fsm.Event.
        public void OnFsmEvent(Fsm fsm, string evName)
        {
            if (_phase == Phase.Idle || fsm == null || fsm.GameObject != _weapon) return;
            switch (_phase)
            {
                case Phase.WaitAnimFinished: if (evName == "AnimFinished") { Plugin.V("AnimFinished"); Finish(); } break;
                case Phase.WaitAnimStart: if (evName == "AnimStart") { Plugin.V("AnimStart"); AfterAnimStart(); } break;
                case Phase.WaitAnimEnd: if (evName == "AnimEnd") { Plugin.V("AnimEnd"); Finish(); } break;
            }
        }

        public void Tick()
        {
            if (_phase == Phase.Idle) return;
            if (_weapon == null || !_weapon.activeInHierarchy) { Plugin.Log.LogWarning("Weapon vanished during unload; aborting"); Finish(); return; }
            if (Time.unscaledTime < _deadline) return;
            Plugin.Log.LogWarning("Unload phase " + _phase + " timed out; continuing");
            if (_phase == Phase.WaitAnimStart) AfterAnimStart(); else Finish();
        }

        private void AfterAnimStart()
        {
            MoveAmmo();
            // Replay the "empty shell" sound the game plays between the open and close animations.
            foreach (var st in _reload.FsmStates)
            {
                if (st.Name.IndexOf("shell", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var a in st.Actions)
                {
                    var ps = a as PlaySound;
                    if (ps == null || ps.clip == null) continue;
                    var clip = ps.clip.Value as AudioClip;
                    if (clip != null) AudioSource.PlayClipAtPoint(clip, _weapon.transform.position + ps.position.Value, ps.volume.Value);
                }
            }
            // State that waits for AnimEnd sends ReloadEnd (close animation).
            var endState = _reload.FsmStates.FirstOrDefault(s => s.Transitions.Any(t => t.EventName == "AnimEnd"));
            if (endState != null) ReplaySendEvents(endState);
            _phase = Phase.WaitAnimEnd;
            _deadline = Time.unscaledTime + Plugin.PhaseTimeout.Value;
        }

        private void MoveAmmo()
        {
            if (_moved) return;
            _moved = true;
            var inGun = _reload.FsmVariables.GetFsmInt("ammo_in_gun");
            var store = _ammoFsm.FsmVariables.GetFsmInt(_caliber);
            if (inGun == null || store == null) return;
            int n = inGun.Value;
            var capVar = _ammoFsm.FsmVariables.GetFsmInt(_caliber + "_capacity");
            int cap = capVar != null ? capVar.Value : int.MaxValue;
            int space = Math.Max(0, cap - store.Value);
            int toStore = Math.Min(n, space);
            int leftover = n - toStore;

            store.Value += toStore;
            inGun.Value = leftover;
            if (leftover > 0)
            {
                if (AmmoBox.Drop(_caliber, leftover, _weapon.transform)) inGun.Value = 0;
                else Plugin.Log.LogWarning("Could not spawn an ammo box; " + leftover + " rounds left in the gun");
            }
            var mirror = _reload.FsmVariables.GetFsmInt("ammo");
            if (mirror != null) mirror.Value = store.Value;
            Plugin.Log.LogInfo("Returned " + toStore + " x " + _caliber + " to backpack (now " + store.Value + "/" + cap + ")" + (leftover > 0 ? ", " + leftover + " dropped as a box" : ""));
        }

        private void Finish()
        {
            MoveAmmo();
            var idle = FindState(_reload, "idle");
            if (idle != null && _weapon != null && _weapon.activeInHierarchy) ReplaySendEvents(idle); // MenuOut / Activate: re-enable firing etc.
            _phase = Phase.Idle;
            _reload = null; _weapon = null;
        }

        private void ReplaySendEvents(FsmState st)
        {
            foreach (var a in st.Actions)
            {
                var se = a as SendEvent;
                if (se == null || !se.Enabled || se.sendEvent == null) continue;
                Plugin.V("  replay " + st.Name + ": " + se.sendEvent.Name);
                try { _reload.Fsm.Event(se.eventTarget, se.sendEvent); }
                catch (Exception e) { Plugin.Log.LogWarning("SendEvent replay failed: " + e.Message); }
            }
        }

        private static FsmState FindState(PlayMakerFSM f, string name)
        {
            foreach (var s in f.FsmStates) if (s.Name == name) return s;
            return null;
        }

        private static PlayMakerFSM FindActiveReloadFsm()
        {
            foreach (var f in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
            {
                if (f.FsmName != "Reload" || !f.gameObject.activeInHierarchy || !f.enabled) continue;
                if (f.Fsm == null || !f.Fsm.Initialized) continue;
                if (f.FsmVariables.GetFsmInt("ammo_in_gun") == null) continue;
                var p = f.transform.parent;
                bool underArm = false;
                while (p != null) { if (p.name == "WeaponsArm") { underArm = true; break; } p = p.parent; }
                if (!underArm) continue;
                if (f.ActiveStateName != "idle") { Plugin.V(f.gameObject.name + " Reload busy (" + f.ActiveStateName + ")"); continue; }
                return f;
            }
            return null;
        }
    }

    /// Spawns an ammo box item (same save-compatible recipe as Apocaspawner) holding the rounds that
    /// did not fit in the backpack.
    internal static class AmmoBox
    {
        private static readonly Dictionary<string, string> DefaultBoxes = new Dictionary<string, string>
        {
            { "ammo_762", "ammo_box_762mm" }, { "ammo_556", "ammo_box_556mm" }, { "ammo_9mm", "ammo_box_9mm" },
            { "ammo_12gauge", "ammo_box_12gauge" }, { "ammo_20gauge", "ammo_box_20gauge" }, { "ammo_22caliber", "ammo_box_22" },
            { "ammo_3006", "ammo_box_3006" }, { "ammo_arrow", "ammo_arrow" },
        };
        private static readonly Dictionary<string, GameObject> _prefabs = new Dictionary<string, GameObject>();

        private static string BoxName(string caliber)
        {
            string d;
            return DefaultBoxes.TryGetValue(caliber, out d) ? d : null;
        }

        private static GameObject FindPrefab(string caliber)
        {
            GameObject cached;
            if (_prefabs.TryGetValue(caliber, out cached) && cached != null) return cached;
            string name = BoxName(caliber);
            if (name == null) return null;
            GameObject asset = null, instance = null;
            foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.transform.parent != null) continue;
                if (!go.scene.IsValid()) { if (go.name == name && asset == null) asset = go; }
                else if (instance == null && go.name.StartsWith(name + "(Clone)") && PlayMakerFSM.FindFsmOnGameObject(go, "Ammo") != null) instance = go;
            }
            var found = asset ?? instance;
            if (found != null) { _prefabs[caliber] = found; Plugin.V("Ammo box template for " + caliber + ": " + found.name + (asset != null ? " (prefab)" : " (scene copy)")); }
            else Plugin.Log.LogWarning("No ammo box prefab named '" + name + "' is loaded");
            return found;
        }

        internal static bool Drop(string caliber, int rounds, Transform at)
        {
            var prefab = FindPrefab(caliber);
            if (prefab == null) return false;

            var cam = Camera.main != null ? Camera.main.transform : at;
            var fwd = Vector3.ProjectOnPlane(cam.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = at.forward;
            var pos = cam.position + fwd * 0.7f - Vector3.up * 0.3f;

            var go = UnityEngine.Object.Instantiate(prefab, pos, UnityEngine.Random.rotation);
            go.SetActive(true);

            // Name + register exactly like the game's own spawner so Easy Save picks it up.
            int id = -1;
            var counterGo = GameObject.Find("itemNameID");
            if (counterGo != null)
            {
                var cf = PlayMakerFSM.FindFsmOnGameObject(counterGo, "itemNameID");
                var counter = cf != null ? cf.FsmVariables.GetFsmInt("intName") : null;
                if (counter != null) { counter.Value += 1; id = counter.Value; }
            }
            go.name = prefab.name.Replace("(Clone)", "") + "(Clone)" + (id >= 0 ? id.ToString() : "");
            if (id < 0) { int i = go.name.IndexOf("(Clone)"); if (i > 0) go.name = go.name.Substring(0, i) + "(Clone)"; }

            var reg = GameObject.Find("NewGO_ArrayList");
            if (reg != null)
            {
                var proxies = reg.GetComponents<PlayMakerArrayListProxy>();
                var list = proxies.FirstOrDefault(p => (p.referenceName ?? "").ToLowerInvariant().Contains("item"))
                           ?? proxies.FirstOrDefault(p => !(p.referenceName ?? "").ToLowerInvariant().Contains("sand"));
                if (list != null) list.arrayList.Add(go);
            }

            var rnd = PlayMakerFSM.FindFsmOnGameObject(go, "Randomize");
            if (rnd != null) rnd.enabled = false;
            var ammo = PlayMakerFSM.FindFsmOnGameObject(go, "Ammo");
            var v = ammo != null ? ammo.FsmVariables.GetFsmInt("ammo") : null;
            if (v == null) { Plugin.Log.LogWarning(go.name + " has no Ammo/ammo variable; destroying it"); UnityEngine.Object.Destroy(go); return false; }
            v.Value = rounds;

            var rb = go.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = false; rb.velocity = fwd * 1.5f; }
            Plugin.Log.LogInfo("Dropped " + go.name + " with " + rounds + " rounds");
            return true;
        }
    }

    // ---------------------------------------------------------------------------------
    // Harmony patches
    // ---------------------------------------------------------------------------------

    /// The game's Reload FSMs poll GetButtonDown("Reload"). We take over that button: it no longer fires on press;
    /// instead we fire the action's event on a short tap (released before HoldSeconds).
    [HarmonyPatch(typeof(GetButtonDown), "OnUpdate")]
    internal static class GetButtonDown_Patch
    {
        private static readonly Dictionary<GetButtonDown, int> _lastTap = new Dictionary<GetButtonDown, int>();

        static bool Prefix(GetButtonDown __instance)
        {
            if (!Plugin.Enabled.Value) return true;
            if (__instance.buttonName == null || __instance.buttonName.Value != "Reload") return true;
            if (Plugin.TapPending())
            {
                int last;
                if (!_lastTap.TryGetValue(__instance, out last) || last != Plugin.TapFrame)
                {
                    _lastTap[__instance] = Plugin.TapFrame;
                    if (__instance.storeResult != null) __instance.storeResult.Value = true;
                    if (__instance.Fsm != null && __instance.sendEvent != null) __instance.Fsm.Event(__instance.sendEvent);
                    return false;
                }
            }
            if (__instance.storeResult != null) __instance.storeResult.Value = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Fsm), "ProcessEvent")]
    internal static class Fsm_Event_Patch
    {
        static void Prefix(Fsm __instance, FsmEvent fsmEvent)
        {
            if (fsmEvent == null || !Runner.Unloader.Busy) return;
            Runner.Unloader.OnFsmEvent(__instance, fsmEvent.Name);
        }
    }
}

//MIT License
//Copyright 2016-Present 
//Ross Tredinnick
//Benny Wysong-Grass
//University of Wisconsin - Madison Virtual Environments Group
//Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), 
//to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, 
//sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, 
//INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
//IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
//TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.


using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using Unity.Netcode;
#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;
#endif

namespace UniCAVE
{
    public class UCNetwork : NetworkBehaviour
    {
        const float MIN_TIME_SCALE = .01f;

        const float MAX_TIME_SCALE = 100f;

        [Tooltip("This object will be transformed by this script")]
        public HeadConfiguration head;

        [Header("Manual navigation (keyboard)")]
        [Tooltip("Lets the head machine move the CAVE root with WASD, Q/E and the arrow keys, for desktop " +
                 "testing. The gamepad is left to GamepadLocomotion.")]
        public bool enableManualNavigation = true;

        [Tooltip("Translation speed in meters/second")]
        public float navMoveSpeed = 1.0f;

        [Tooltip("Rotation speed in degrees/second")]
        public float navRotateSpeed = 15.0f;

        [Tooltip("Speed multiplier while Shift is held")]
        public float navSprintMultiplier = 2.0f;

        [Tooltip("Ignore gamepad stick input below this magnitude (0-1)")]
        public float gamepadDeadZone = 0.15f;

        private float lastTime = 0.0f;

        private bool syncedRandomSeed = false;

        private int frameCount = 0;

        private uint poseSeq = 0;
        private int lastSentReplayStep = -1;

        /// <summary>
        /// Sequence number and server send time of the last pose this node applied. Nodes that applied
        /// the same broadcast report the same pair, so per-node logs can be matched frame by frame.
        /// </summary>
        public static uint LastPoseSeq { get; private set; }

        public static float LastPoseTime { get; private set; }

        /// <summary>Head position this node last applied.</summary>
        public static Vector3 LastHeadPos { get; private set; }

        public static Vector3 LastRootPos { get; private set; }

        /// <summary>True on a display node while the head has hidden the overlays (F1). Set on the head only if it has a local display.</summary>
        public static bool DisplayOverlaysHidden { get; private set; }

        /// <summary>True when this instance drives at least one display (ARCADE head, Editor), false on a display-less head (CAVE2MASTER).</summary>
        public static bool HasLocalDisplay { get; private set; }

        private bool overlaysHiddenOnDisplays;

        void Awake()
        {
            HasLocalDisplay = GetAllDisplays().Exists(d => d.ShouldBeActive());
        }

        private bool CanSendRpc
        {
            get
            {
                return IsServer && NetworkManager != null && NetworkManager.IsListening && IsSpawned;
            }
        }

        /// <summary>
        /// If server, broadcast information to all clients.
        /// </summary>
        void Update()
        {
            if(IsServer)
            {
                if(Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
                {
                    if(CanSendRpc)
                    {
                        QuitApplicationClientRpc();
                    }
                    Application.Quit();
                }

                if(!CanSendRpc)
                {
                    return;
                }

                // The new root pose is broadcast below, so the clients follow.
                HandleManualNavigation();

                // F5 toggles stereo/mono on every node.
                if(head != null && Keyboard.current != null && Keyboard.current.f5Key.wasPressedThisFrame)
                {
                    head.mono = !head.mono;
                    SetMonoClientRpc(head.mono);
                }

                // F1 hides on-screen overlays on every node with a display; a display-less head keeps its own.
                if(Keyboard.current != null && Keyboard.current.f1Key.wasPressedThisFrame)
                {
                    overlaysHiddenOnDisplays = !overlaysHiddenOnDisplays;
                    if(HasLocalDisplay) DisplayOverlaysHidden = overlaysHiddenOnDisplays;
                    SetOverlaysHiddenClientRpc(overlaysHiddenOnDisplays);
                }

                // While a replay gate holds the path the pose does not change, so it is not resent.
                if(AutoNavigator.ReplayActive && AutoNavigator.LastReplayStep == lastSentReplayStep) return;
                lastSentReplayStep = AutoNavigator.LastReplayStep;

                poseSeq++;
                LastPoseSeq = poseSeq;
                LastPoseTime = Time.time;
                LastHeadPos = head != null ? head.transform.position : Vector3.zero;
                LastRootPos = transform.position;

                if(head != null)
                {
                    SetTransformsClientRpc(transform.position, transform.rotation, head.transform.position, head.transform.rotation, poseSeq, Time.time, AutoNavigator.LastReplayStep);
                }
                else
                {
                    SetTransformsClientRpc(transform.position, transform.rotation, Vector3.zero, Quaternion.identity, poseSeq, Time.time, AutoNavigator.LastReplayStep);
                }

                SetTimeClientRpc(Time.time);

                if(!syncedRandomSeed && frameCount > 600)
                {
                    SetRandomSeedClientRpc((int)UnityEngine.Random.Range(int.MinValue, int.MaxValue));
                    syncedRandomSeed = true;
                }

                frameCount++;
            }
        }

        /// <summary>
        /// Server-only keyboard navigation: WASD moves, Q/E lowers and raises, the arrow keys rotate.
        /// <see cref="SetTransformsClientRpc"/> replicates the result.
        /// </summary>
        void HandleManualNavigation()
        {
            if(!enableManualNavigation) return;

            float dt = Time.deltaTime;

            Vector2 moveInput = Vector2.zero;   // x = strafe (right +), y = forward (+)
            Vector2 lookInput = Vector2.zero;   // x = yaw (right +), y = pitch (nose up +)
            float verticalInput = 0f;           // up +, down -
            bool sprint = false;

            Keyboard kb = Keyboard.current;
            if(kb != null)
            {
                if(kb.wKey.isPressed) moveInput.y += 1f;
                if(kb.sKey.isPressed) moveInput.y -= 1f;
                if(kb.dKey.isPressed) moveInput.x += 1f;
                if(kb.aKey.isPressed) moveInput.x -= 1f;

                if(kb.rightArrowKey.isPressed) lookInput.x += 1f;
                if(kb.leftArrowKey.isPressed) lookInput.x -= 1f;
                if(kb.upArrowKey.isPressed) lookInput.y += 1f;
                if(kb.downArrowKey.isPressed) lookInput.y -= 1f;

                if(kb.eKey.isPressed) verticalInput += 1f;
                if(kb.qKey.isPressed) verticalInput -= 1f;

                if(kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) sprint = true;
            }

            moveInput = Vector2.ClampMagnitude(moveInput, 1f);
            lookInput = Vector2.ClampMagnitude(lookInput, 1f);
            verticalInput = Mathf.Clamp(verticalInput, -1f, 1f);

            float speed = navMoveSpeed * (sprint ? navSprintMultiplier : 1f);

            // Nose-up input is a negative rotation about the local right axis.
            if(Mathf.Abs(lookInput.x) > 0.001f)
                transform.Rotate(Vector3.up, lookInput.x * navRotateSpeed * dt, Space.World);
            if(Mathf.Abs(lookInput.y) > 0.001f)
                transform.Rotate(Vector3.right, -lookInput.y * navRotateSpeed * dt, Space.Self);

            // Flip a sign here if an axis is reversed on your rig.
            Vector3 move = (transform.forward * moveInput.y)
                         + (transform.right * moveInput.x)
                         + (Vector3.up * verticalInput);
            if(move.sqrMagnitude > 0.000001f)
                transform.position += move * speed * dt;
        }

        /// <summary>
        /// Set transform of CAVE and head object.
        /// </summary>
        /// <param name="myPos">global CAVE position</param>
        /// <param name="myOri">gobal CAVE orientation</param>
        /// <param name="headPos">global head position</param>
        /// <param name="headOri">global head orientation</param>
        /// <param name="seq">monotonic id of this pose sample</param>
        /// <param name="sendTime">server Time.time when the sample was sent</param>
        /// <param name="replayStep">replay step id this pose belongs to; 0 outside a replay run</param>
        [ClientRpc]
        void SetTransformsClientRpc(Vector3 myPos, Quaternion myOri, Vector3 headPos, Quaternion headOri, uint seq, float sendTime, int replayStep)
        {
            transform.SetPositionAndRotation(myPos, myOri);

            if(head != null)
            {
                head.transform.SetPositionAndRotation(headPos, headOri);
            }

            LastPoseSeq = seq;
            LastPoseTime = sendTime;
            LastHeadPos = headPos;
            LastRootPos = myPos;
            // Same message as the pose, so the step and the pose always match.
            AutoNavigator.LastReplayStep = replayStep;
        }

        /// <summary>
        /// Set unity time, mainly for video players.
        /// </summary>
        /// <param name="canonicalTime">time since start</param>
        [ClientRpc]
        void SetTimeClientRpc(float canonicalTime)
        {
            if(lastTime == 0.0f)
            {
                lastTime = canonicalTime;
            }
            else
            {
                float ourTime = Time.time;
                float timeDiff = canonicalTime - lastTime;
                float scale = ((canonicalTime - ourTime) + timeDiff) / timeDiff;

                lastTime = canonicalTime;

                if(scale < 0.0f)
                {
                    scale = MIN_TIME_SCALE;
                }
                else if(scale > 100.0f)
                {
                    scale = MAX_TIME_SCALE;
                }

                Time.timeScale = scale;
            }
        }

        /// <summary>
        /// Initialize Unity's random number generator with a seed.
        /// <para>Also restarts all ParticleSystems with the new seed.</para>
        /// </summary>
        /// <param name="seed">the seed</param>
        [ClientRpc]
        void SetRandomSeedClientRpc(int seed)
        {
            ParticleSystem[] particleSystems = FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
            foreach(ParticleSystem ps in particleSystems)
            {
                ps.Stop();
            }

            Random.InitState(seed);

            foreach(ParticleSystem ps in particleSystems)
            {
                ps.useAutoRandomSeed = false;
                ps.randomSeed = (uint)seed;
                ps.Simulate(0.0f, true, true, false);
                ps.Play();
            }

            Debug.Log($"Synced random seed to {seed}.");
        }

        /// <summary>
        /// Mirror the head's stereo/mono flag on every client.
        /// </summary>
        [ClientRpc]
        void SetMonoClientRpc(bool mono)
        {
            if(head != null) head.mono = mono;
        }

        /// <summary>
        /// Hide or show the overlays on the display nodes. A host also runs ClientRpcs, so it skips itself.
        /// </summary>
        [ClientRpc]
        void SetOverlaysHiddenClientRpc(bool hidden)
        {
            if(IsServer) return;
            DisplayOverlaysHidden = hidden;
        }

        /// <summary>
        /// Shutdown Unity.
        /// </summary>
        [ClientRpc]
        void QuitApplicationClientRpc()
        {
            Application.Quit();
        }

        /// <summary>
        /// Returns true if all PhysicalDisplays are initialized, false otherwise.
        /// </summary>
        public bool Initialized
        {
            get
            {
                foreach(PhysicalDisplay disp in GetAllDisplays())
                {
                    if(disp.enabled && disp.gameObject.activeSelf)
                    {
                        if(!disp.Initialized()) return false;
                    }
                }
                return true;
            }
        }

        [Tooltip("You can load PhysicalDisplay settings for all children recursively, right click the name of this script and settings will be loaded from this file path")]
        public string settingsToLoad;

        [ContextMenu("Load Settings For All Children")]
        void LoadSettingsChildren()
        {
            LoadSettingsChildren_h(null);
        }

        void LoadSettingsChildren_h(GameObject it = null)
        {
            if(it == null) it = gameObject;

            for(int i = 0; i < it.transform.childCount; i++)
            {
                GameObject child = it.transform.GetChild(i).gameObject;
                PhysicalDisplay physicalDisplay = GetComponent<PhysicalDisplay>();
                if(physicalDisplay)
                {
                    physicalDisplay.TryToDeSerialize(settingsToLoad);
                }
                LoadSettingsChildren_h(child);
            }
        }

        /// <summary>
        /// Return all displays associated with this network.
        /// </summary>
        /// <returns>all displays associated</returns>
        public List<PhysicalDisplay> GetAllDisplays()
        {
            List<PhysicalDisplay> disps = new List<PhysicalDisplay>();
            List<PhysicalDisplayManager> managers = new List<PhysicalDisplayManager>();
            IterateAllRelevantChildren(gameObject, disps, managers);
            return disps;
        }

        /// <summary>
        /// Produce a Windows Powershell script that can be invoked on any machine to properly start the app.
        /// </summary>
        /// <returns>the powershell launch script</returns>
        public string GenerateLaunchScript()
        {
            List<PhysicalDisplay> displays = new List<PhysicalDisplay>();
            List<PhysicalDisplayManager> managers = new List<PhysicalDisplayManager>();
            IterateAllRelevantChildren(gameObject, displays, managers);

            string res = "# Windows Powershell Launch Script\n";
            res += "# Script Generated On " + System.DateTime.Now.ToLongDateString() + ", " + System.DateTime.Now.ToLongTimeString() + "\n";
            res += "# Setup contains " + displays.Count + " displays and " + managers.Count + " display managers";

            for(int i = 0; i < displays.Count; ++i)
            {
                if(displays[i].manager != null) continue;

                res += "\n\n# Display: " + displays[i].name;
                res += "\nIf ($env:ComputerName -eq '" + displays[i].machineName + "') {";
                if(displays[i].dualPipe && displays[i].dualInstance)
                {
                    for(int j = 0; j < 2; j++)
                    {
                        res += "\n\t& '.\\" + Application.productName + ".exe'";
                        res += " " + (displays[i].exclusiveFullscreen ? "-screen-fullscreen 1 -adapter " + displays[i].display : "-screen-fullscreen 0 -popupwindow");
                        res += " " + ((displays[i].is3D && !displays[i].dualPipe) ? "-vrmode stereo" : "");
                        res += " " + "eye " + (j == 0 ? "left" : "right");
                    }
                }
                else
                {
                    res += "\n\t& '.\\" + Application.productName + ".exe'";
                    res += " " + (displays[i].exclusiveFullscreen ? "-screen-fullscreen 1 -adapter " + displays[i].display : "-screen-fullscreen 0 -popupwindow");
                    res += " " + ((displays[i].is3D && !displays[i].dualPipe) ? "-vrmode stereo" : "");
                }

                res += "\n}";
            }

            for(int i = 0; i < managers.Count; i++)
            {
                res += "\n\n# Display Group: " + managers[i].name;
                res += "\nIf ($env:ComputerName -eq '" + managers[i].machineName + "') {";

                res += "\n\t& '.\\" + Application.productName + ".exe'";
                res += " " + (managers[i].fullscreen ? ("-screen-fullscreen 1 -adapter " + managers[i].displayNumber) : ("-screen-fullscreen 0 -popupwindow"));
                res += " " + "-screen-width " + managers[i].displayResolution.x + " -screen-height " + managers[i].displayResolution.y;
                res += " " + ((displays[0].is3D && !displays[0].dualPipe) ? "-vrmode stereo" : "");

                res += "\n}";
            }

            return res;
        }

        /// <summary>
        /// Recursively searches child tree for PhysicalDisplays and PhysicalDisplayManagers and adds them to the provided lists.
        /// </summary>
        /// <param name="it">Start iterating from</param>
        /// <param name="displays">List of displays to add to</param>
        /// <param name="managers">List of managers to add to</param>
        private static void IterateAllRelevantChildren(GameObject it, List<PhysicalDisplay> displays, List<PhysicalDisplayManager> managers)
        {
            for(int i = 0; i < it.transform.childCount; i++)
            {
                GameObject child = it.transform.GetChild(i).gameObject;

                PhysicalDisplay pd = child.GetComponent<PhysicalDisplay>();
                if(pd)
                {
                    displays.Add(pd);
                }

                PhysicalDisplayManager pdm = child.GetComponent<PhysicalDisplayManager>();
                if(pdm)
                {
                    managers.Add(pdm);
                }

                IterateAllRelevantChildren(child, displays, managers);
            }
        }

#if UNITY_EDITOR
        [CustomEditor(typeof(UCNetwork))]
        public class Editor : UnityEditor.Editor
        {
            private Material material;
            #pragma warning disable CS0414
            private int selectedIndex = 0;
            #pragma warning restore CS0414

            void OnEnable()
            {
                //this is used for rendering, don't remove
                // URP-compatible shader, with a fallback
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if(shader == null) shader = Shader.Find("Sprites/Default");
                if(shader != null) material = new Material(shader);
            }

            private void TextAtPosition(int x, int y, int height, string text)
            {
                GUIStyle myStyle = new GUIStyle();

                myStyle.fontSize = height;
                myStyle.alignment = TextAnchor.UpperLeft;


                //Color32 color = Color.red;
                //EditorGUI.DrawRect(new Rect(x - 11, y, text.Length * 16 + 11, 32 - 2), color);
                //Rect r = GUILayoutUtility.GetLastRect();
                EditorGUI.SelectableLabel(new Rect(x, y, text.Length * 16, height), text, myStyle);
            }

            private List<PhysicalDisplay> GetAllDisplays()
            {
                List<PhysicalDisplay> disps = new List<PhysicalDisplay>();
                List<PhysicalDisplayManager> managers = new List<PhysicalDisplayManager>();
                IterateAllRelevantChildren((target as UCNetwork).gameObject, disps, managers);
                return disps;
            }
            private List<PhysicalDisplayManager> GetAllManagers()
            {
                List<PhysicalDisplay> disps = new List<PhysicalDisplay>();
                List<PhysicalDisplayManager> managers = new List<PhysicalDisplayManager>();
                IterateAllRelevantChildren((target as UCNetwork).gameObject, disps, managers);
                return managers;
            }

            public override void OnInspectorGUI()
            {
                UCNetwork cave = target as UCNetwork;

                cave.head = (HeadConfiguration)EditorGUILayout.ObjectField("Head", cave.head, typeof(HeadConfiguration), true);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Manual Navigation (keyboard)", EditorStyles.boldLabel);
                cave.enableManualNavigation = EditorGUILayout.Toggle(new GUIContent("Enable Manual Navigation", "Move the CAVE root with WASD, Q/E and the arrow keys on the head machine."), cave.enableManualNavigation);
                using(new EditorGUI.DisabledScope(!cave.enableManualNavigation))
                {
                    cave.navMoveSpeed = EditorGUILayout.FloatField(new GUIContent("Move Speed", "Translation speed in meters/second"), cave.navMoveSpeed);
                    cave.navRotateSpeed = EditorGUILayout.FloatField(new GUIContent("Rotate Speed", "Rotation speed in degrees/second"), cave.navRotateSpeed);
                    cave.navSprintMultiplier = EditorGUILayout.FloatField(new GUIContent("Sprint Multiplier", "Speed multiplier while Shift is held"), cave.navSprintMultiplier);
                    cave.gamepadDeadZone = EditorGUILayout.Slider(new GUIContent("Gamepad Dead Zone", "Ignore gamepad stick input below this magnitude"), cave.gamepadDeadZone, 0f, 0.9f);
                }
                EditorGUILayout.Space();

                if(GUILayout.Button("Save Launch Script"))
                {
                    string launchScript = cave.GenerateLaunchScript();
                    string savePath = EditorUtility.SaveFilePanel("Save Launch Script", "./", Application.productName + ".ps1", "ps1");
                    if(savePath != null && savePath.Length != 0)
                    {
                        System.IO.File.WriteAllText(savePath, launchScript);
                        Debug.Log("Saved launch script to " + savePath);
                    }
                    else
                    {
                        Debug.Log("Didn't save file (no path given)");
                    }
                }
                List<PhysicalDisplay> displays = GetAllDisplays();
                List<PhysicalDisplayManager> managers = GetAllManagers();
                Dictionary<string, List<object>> havingName = new Dictionary<string, List<object>>();
                List<string> machines = new List<string>();
                for(int i = 0; i < managers.Count; i++)
                {
                    if(!havingName.ContainsKey(managers[i].machineName))
                    {
                        havingName[managers[i].machineName] = new List<object> { managers[i] };
                        machines.Add(managers[i].machineName);
                    }
                    else
                    {
                        havingName[managers[i].machineName].Add(managers[i].gameObject);
                    }
                }
                for(int i = 0; i < displays.Count; i++)
                {
                    if(displays[i].manager == null)
                    {
                        if(displays[i].machineName == null || !havingName.ContainsKey(displays[i].machineName))
                        {
                            havingName[displays[i].machineName] = new List<object> { displays[i] };
                            machines.Add(displays[i].machineName);
                        }
                        else
                        {
                            havingName[displays[i].machineName].Add(displays[i].gameObject);
                        }
                    }
                }

                //List<string> errors = new List<string>();
                //foreach(var kvp in havingName) {
                //    if(kvp.Value.Count > 1) {
                //        errors.Add("These GameObjects have conflicting use of machine name " + kvp.Key + " :");
                //        foreach(var obj in kvp.Value) {
                //            errors.Add("\t" + Util.ObjectFullName(obj));
                //        }
                //    }
                //}

                //if(errors.Count != 0) {
                //    GUIStyle colored = new GUIStyle();
                //    colored.fontSize = 18;
                //    colored.normal.textColor = new Color(0.7f, 0, 0);
                //    EditorGUILayout.LabelField("WARNING: Invalid CAVE Configuration", colored);
                //    foreach (var er in errors) {
                //        EditorGUILayout.LabelField(er);
                //    }
                //    return;
                //}

                //if (selectedIndex >= machines.Count) selectedIndex = 0;
                //selectedIndex = EditorGUILayout.Popup("Selected Machine", selectedIndex, machines.ToArray(), EditorStyles.popup);

                /*
                List<object> selectedObjs = havingName[machines[selectedIndex]];
                Dictionary<int, List<KeyValuePair<string, RectInt>>> usingDisplay = new Dictionary<int, List<KeyValuePair<string, RectInt>>>();
                for (int i = 0; i < selectedObjs.Count; i++) {
                    int displayIndex = -1;
                    List<RectInt> viewports = new List<RectInt>();
                    if (selectedObjs[i] is PhysicalDisplayManager) {
                        displayIndex = (selectedObjs[i] as PhysicalDisplayManager).displayNumber;
                        foreach (PhysicalDisplay disp in (selectedObjs[i] as PhysicalDisplayManager).displays) {
                            viewports.Add(new KeyValuePair<string, RectInt>());
                        }
                    } else if (selectedObjs[i] is PhysicalDisplay) {
                        if ((selectedObjs[i] as PhysicalDisplay).exclusiveFullscreen) {
                            displayIndex = (selectedObjs[i] as PhysicalDisplay).display;
                        }
                    }
                    if (usingDisplay.ContainsKey(displayIndex)) {
                        usingDisplay[displayIndex].Add(sele)
                    } else {
                        usingDisplay[displayIndex] = new List<RectInt> { }
                    }
                }
                for (int i = 0; i < selectedObjs.Count; i++) {
                    selectedObjs[i].GetComponent < PhysicalDisplayManager >
                    int dispIndex = managers[i].displayNumber;

                    EditorGUILayout.LabelField("Display " + dispIndex + " :");
                    GUILayout.BeginHorizontal(EditorStyles.helpBox);
                    Rect drawSpace = GUILayoutUtility.GetRect(10, 10000, 200, 200);
                    if (Event.current.type == EventType.Repaint) {
                        GUI.BeginClip(drawSpace);
                        GL.PushMatrix();

                        //GL.Viewport(drawSpace);
                        //GL.Clear(true, false, Color.black);
                        //material.SetPass(0);

                        //GL.Begin(GL.QUADS);
                        //GL.Color(Color.white);
                        //    GL.Vertex3(0,               0,                  0);
                        //    GL.Vertex3(drawSpace.width, 0,                  0);
                        //    GL.Vertex3(drawSpace.width, drawSpace.height,   0);
                        //    GL.Vertex3(0,               drawSpace.height,   0);
                        //GL.End();

                        TextAtPosition(0, 0, 12, "long ass string");

                        GL.PopMatrix();
                        GUI.EndClip();
                    }
                    GUILayout.EndHorizontal();
                }*/

                if(GUI.changed)
                {
                    EditorUtility.SetDirty(cave);
                    EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
                }
            }
        }
#endif
    }
}
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
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UniCAVE
{
    /// <summary>
    /// Starts the program as either client or server depending on machine
    /// </summary>
    public class NetworkInitialization : MonoBehaviour
    {
        public static int TimeoutWaitTime = 20; //make this changeable from command line?

        public NetworkManager networkManager;

        [Tooltip("Caps the main loop at this frame rate; match the display refresh rate. It also " +
                 "applies on a head machine that renders nothing, where VSync has no effect.")]
        public int targetFrameRate = 240; 

        [SerializeField]
        [UnityEngine.Serialization.FormerlySerializedAs("headMachine")]
        string oldHeadMachine = "C6_V1_HEAD";

        [SerializeField]
        MachineName headMachineAsset;

        public string headMachine => MachineName.GetMachineName(oldHeadMachine, headMachineAsset);

        [Tooltip("This can be overriden at runtime with parameter serverAddress, for example \"serverAddress 192.168.0.100\"")]
        public string serverAddress = "192.168.4.140";

        [Tooltip("This can be overriden at runtime with parameter serverPort, for example \"serverPort 8421\"")]
        public int serverPort = 7568;

        /// <summary>
        /// Starts as client or server.
        /// </summary>
        void Start()
        {
            // VSync does nothing on a head machine that renders nothing, so cap the loop here.
            if(targetFrameRate > 0)
            {
                Application.targetFrameRate = targetFrameRate;
            }

            string serverArg = Util.GetArg("serverAddress");
            if(serverArg != null)
            {
                serverAddress = serverArg;
            }

            string portArg = Util.GetArg("serverPort");
            if(portArg != null)
            {
                int.TryParse(portArg, out serverPort);
            }

            string runningMachineName = Util.GetMachineName();
            Debug.Log($"serverAddress = {serverAddress}, serverPort = {serverPort}, headMachine = {headMachine}, runningMachine = {runningMachineName}");

            var transport = networkManager.GetComponent<UnityTransport>();
            if(transport != null)
            {
                transport.SetConnectionData(serverAddress, (ushort)serverPort);
            }

#if !UNITY_EDITOR
            if ((Util.GetArg("forceClient") == "1") || !string.Equals(Util.GetMachineName(), headMachine, System.StringComparison.OrdinalIgnoreCase))
            {
                networkManager.StartClient();
            }
            else
            {
                networkManager.StartServer();
            }
#else
            networkManager.StartServer();
#endif
        }

        void OnDestroy()
        {
            // Shut down cleanly, or the native NetworkDriver allocations are reported as leaks on exit.
            if (networkManager != null && (networkManager.IsClient || networkManager.IsServer || networkManager.IsListening))
            {
                networkManager.Shutdown();
            }
        }

        /// <summary>
        /// Quits after 20 seconds if no connection is made to server.
        /// </summary>
        void Update()
        {
#if !UNITY_EDITOR
            if(networkManager == null || networkManager.ShutdownInProgress)
                return;

            if(!string.Equals(Util.GetMachineName(), headMachine, System.StringComparison.OrdinalIgnoreCase))
            {
                if(!networkManager.IsClient && !networkManager.IsServer)
                {
                    networkManager.StartClient();
                }

                if(Time.time > TimeoutWaitTime && !networkManager.IsConnectedClient)
                {
                    Debug.Log("NetworkInitialization: Client failed to connect after " + TimeoutWaitTime + "s. Quitting.");
                    Application.Quit();
                }
            }
#endif
        }

#if UNITY_EDITOR
        [CanEditMultipleObjects]
        [CustomEditor(typeof(NetworkInitialization))]
        class Editor : UnityEditor.Editor
        {
            public override void OnInspectorGUI()
            {
                serializedObject.Update();

                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(networkManager)));

                //special handling for old machine names:
                SerializedProperty oldMachineName = serializedObject.FindProperty(nameof(oldHeadMachine));
                SerializedProperty machineName = serializedObject.FindProperty(nameof(headMachineAsset));
                MachineName.DrawDeprecatedMachineName(oldMachineName, machineName, "HeadMachine");

                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(serverAddress)));

                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(serverPort)));

                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(targetFrameRate)));

                serializedObject.ApplyModifiedProperties();
            }
        }
#endif
    }
}
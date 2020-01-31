﻿using UnityEngine;

namespace BundleSystem
{
    public class BundleDebugGUI : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        public bool ShowGUI = true;

        private void OnGUI()
        {
            if (!ShowGUI) return;
            BundleManager.DrawDebugGUI();
        }
    }

    public partial class BundleManager : MonoBehaviour
    {
        internal static void DrawDebugGUI()
        {
            GUILayout.Label("Bundle RefCounts");
            GUILayout.Label("-----------------");
            foreach (var kv in s_BundleRefCounts)
            {
                if (kv.Value == 0) continue;
                GUILayout.Label($"Name : {kv.Key} - {kv.Value}");
            }

            if(Application.isEditor)
            {
                GUILayout.Label("-----------------");
                GUILayout.Label("Bundle UseCounts");
                GUILayout.Label("-----------------");
                foreach (var kv in s_BundleDirectUseCount)
                {
                    if (kv.Value == 0) continue;
                    GUILayout.Label($"Name : {kv.Key} - {kv.Value}");
                }
            }

            GUILayout.Label("-----------------");
            GUILayout.Label($"Tracking Object Count {s_TrackingObjects.Count}");
            GUILayout.Label($"Tracking Owner Count {s_TrackingOwners.Count}");
        }
    }
}

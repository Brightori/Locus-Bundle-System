﻿using System.Collections.Generic;
using UnityEngine;
using System.Collections;
using UnityEngine.Networking;
using System.IO;

namespace BundleSystem
{
    /// <summary>
    /// Handle Resources expecially assetbundles.
    /// Also works in editor
    /// </summary>
    public partial class BundleManager : MonoBehaviour
    {
        //instance is almost only for coroutines
        public static BundleManager Instance { get; private set; }

        class LoadedBundle
        {
            public string Name;
            public AssetBundle Bundle;
            public Hash128 Hash;
            public HashSet<string> AssetNames;
            public List<string> Dependencies; //including self
            public bool IsLocalBundle;
            public string LoadPath;
            public UnityWebRequest RequestForReload;
            public bool IsReloading = false;
            public LoadedBundle(AssetbundleBuildManifest.BundleInfo info, string loadPath, AssetBundle bundle, bool isLocal)
            {
                Name = info.BundleName;
                IsLocalBundle = isLocal;
                LoadPath = loadPath;
                Bundle = bundle; 
                Hash = info.Hash;
                AssetNames = new HashSet<string>(bundle.GetAllAssetNames());
                Dependencies = info.Dependencies;
            }
        }

        //Asset bundles that is loaded keep it static so we can easily call this in static method
        static Dictionary<string, LoadedBundle> s_AssetBundles = new Dictionary<string, LoadedBundle>();
        static Dictionary<string, Hash128> s_LocalBundles = new Dictionary<string, Hash128>();
        static Dictionary<string, LoadedBundle> s_SceneNames = new Dictionary<string, LoadedBundle>();

        public static bool UseAssetDatabase { get; private set; } = false;
        public static bool Initialized { get; private set; } = false;
        public static string LocalURL { get; private set; }
        public static string RemoteURL { get; private set; }
        public static string GlobalBundleHash { get; private set; }

        public static bool AutoReloadBundle { get; private set; } = true;
        public static bool LogMessages { get; private set; } = false;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Setup()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += TrackOnSceneLoaded;
            UnityEngine.SceneManagement.SceneManager.sceneUnloaded += TrackOnSceneUnLoaded;
            Instance = new GameObject("_BundleManager").AddComponent<BundleManager>();
            DontDestroyOnLoad(Instance.gameObject);
            LocalURL = Application.platform == RuntimePlatform.IPhonePlayer ? "file://" + AssetbundleBuildSettings.LocalBundleRuntimePath : AssetbundleBuildSettings.LocalBundleRuntimePath;
#if UNITY_EDITOR
            SetupAssetdatabaseUsage();
            LocalURL = Path.Combine(s_EditorBuildSettings.LocalOutputPath, UnityEditor.EditorUserBuildSettings.activeBuildTarget.ToString());
#endif
        }

        static void CollectSceneNames(LoadedBundle loadedBundle)
        {
            var scenes = loadedBundle.Bundle.GetAllScenePaths();
            foreach (var scene in scenes) s_SceneNames[scene] = loadedBundle;
        }

        private void OnDestroy()
        {
            foreach (var kv in s_AssetBundles)
                kv.Value.Bundle.Unload(false);
            s_AssetBundles.Clear();
        }

        public BundleAsyncOperation Initialize(bool autoReloadBundle = true, bool logMessages = false)
        {
            var result = new BundleAsyncOperation();
            StartCoroutine(CoInitalizeLocalBundles(result, autoReloadBundle, logMessages));
            return result;
        }

        IEnumerator CoInitalizeLocalBundles(BundleAsyncOperation result, bool autoReloadBundle, bool logMessages)
        {
            if(Initialized)
            {
                result.Done(true);
                yield break;
            }

            LogMessages = logMessages;
            AutoReloadBundle = autoReloadBundle;

            if(UseAssetDatabase)
            {
                Initialized = true;
                result.Done(true);
                yield break;
            }

            if(LogMessages) Debug.Log($"LocalURL : {LocalURL}");


            foreach (var kv in s_AssetBundles)
                kv.Value.Bundle.Unload(false);
            s_SceneNames.Clear();
            s_AssetBundles.Clear();
            s_LocalBundles.Clear();

            var manifestReq = UnityWebRequest.Get(Path.Combine(LocalURL, AssetbundleBuildSettings.ManifestFileName));
            yield return manifestReq.SendWebRequest();
            if (manifestReq.isHttpError || manifestReq.isNetworkError)
            {
                result.Done(false);
                yield break;
            }

            if(!AssetbundleBuildManifest.TryParse(manifestReq.downloadHandler.text, out var localManifest))
            {
                result.Done(false);
                yield break;
            }

            //cached version is recent one.
            var cacheIsValid = AssetbundleBuildManifest.TryParse(PlayerPrefs.GetString("CachedManifest", string.Empty), out var cachedManifest) 
                && cachedManifest.BundleVersion == localManifest.BundleVersion;

            result.SetIndexLength(localManifest.BundleInfos.Count);
            for(int i = 0; i < localManifest.BundleInfos.Count; i++)
            {
                result.SetCurrentIndex(i);
                AssetbundleBuildManifest.BundleInfo bundleInfoToLoad;
                AssetbundleBuildManifest.BundleInfo cachedBundleInfo = default;
                var localBundleInfo = localManifest.BundleInfos[i];

                bool useLocalBundle =
                    !cacheIsValid || //cache is not valid or...
                    !cachedManifest.TryGetBundleInfo(localBundleInfo.BundleName, out cachedBundleInfo) || //missing bundle or... 
                    !Caching.IsVersionCached(cachedBundleInfo.AsCached); //is not cached no unusable.

                bundleInfoToLoad = useLocalBundle ? localBundleInfo : cachedBundleInfo;
                var loadPath = Path.Combine(LocalURL, bundleInfoToLoad.BundleName);

                var bundleReq = UnityWebRequestAssetBundle.GetAssetBundle(loadPath, bundleInfoToLoad.Hash);
                var bundleOp = bundleReq.SendWebRequest();
                while (!bundleOp.isDone)
                {
                    result.SetProgress(bundleOp.progress);
                    yield return null;
                }

                if (!bundleReq.isHttpError && !bundleReq.isNetworkError)
                {
                    var loadedBundle = new LoadedBundle(bundleInfoToLoad, loadPath, DownloadHandlerAssetBundle.GetContent(bundleReq), useLocalBundle);
                    s_AssetBundles.Add(localBundleInfo.BundleName, loadedBundle);
                    CollectSceneNames(loadedBundle);

                    if (LogMessages) Debug.Log($"Local bundle Loaded - Name : {localBundleInfo.BundleName}, Hash : {bundleInfoToLoad.Hash }");
                }
                else
                {
                    result.Done(false);
                    yield break;
                }

                bundleReq.Dispose();
                s_LocalBundles.Add(localBundleInfo.BundleName, localBundleInfo.Hash);
            }

            RemoteURL = Path.Combine(localManifest.RemoteURL, localManifest.BuildTarget);
            Initialized = true;
            if (LogMessages) Debug.Log($"Initialize Success \nRemote URL : {RemoteURL} \nLocal URL : {LocalURL}");
            result.Done(true);
        }

        public BundleAsyncOperation<long> GetDownloadSize()
        {
            var result = new BundleAsyncOperation<long>();
            StartCoroutine(GetDownloadSize(result));
            return result;
        }

        IEnumerator GetDownloadSize(BundleAsyncOperation<long> result)
        {
            if (!Initialized)
            {
                Debug.LogError("Do Initialize first");
                result.Done(false);
                yield break;
            }

            if (UseAssetDatabase)
            {
                result.Done(true);
                yield break;
            }

            var manifestReq = UnityWebRequest.Get(Path.Combine(RemoteURL, AssetbundleBuildSettings.ManifestFileName));
            yield return manifestReq.SendWebRequest();

            if (manifestReq.isHttpError || manifestReq.isNetworkError)
            {
                result.Done(false);
                yield break;
            }

            var remoteManifestJson = manifestReq.downloadHandler.text;
            manifestReq.Dispose();

            if (!AssetbundleBuildManifest.TryParse(remoteManifestJson, out var remoteManifest))
            {
                result.Done(false);
                yield break;
            }

            long totalSize = 0;

            for (int i = 0; i < remoteManifest.BundleInfos.Count; i++)
            {
                result.SetCurrentIndex(i);
                var bundleInfo = remoteManifest.BundleInfos[i];
                var localBundle = s_LocalBundles.TryGetValue(bundleInfo.BundleName, out var localHash) && localHash == bundleInfo.Hash;
                if (!localBundle && !Caching.IsVersionCached(bundleInfo.AsCached))
                    totalSize += bundleInfo.Size;
            }

            result.Result = totalSize;
            result.Done(true);
        }

        /// <summary>
        /// acutally download assetbundles load from cache if cached
        /// </summary>
        /// <param name="hardUnload">hard unload reloaded bundle</param>
        /// <returns>returns bundle has been reloaded</returns>
        public BundleAsyncOperation<bool> DownloadAssetBundles(bool hardUnload = false)
        {
            var result = new BundleAsyncOperation<bool>();
            StartCoroutine(CoDownloadAssetBundles(hardUnload, result));
            return result;
        }

        IEnumerator CoDownloadAssetBundles(bool hardUnload, BundleAsyncOperation<bool> result)
        {
            if (!Initialized)
            {
                Debug.LogError("Do Initialize first");
                result.Done(false);
                yield break;
            }

            if(UseAssetDatabase)
            {
                result.Done(true);
                yield break;
            }

            var startTime = Time.realtimeSinceStartup;

            var manifestReq = UnityWebRequest.Get(Path.Combine(RemoteURL, AssetbundleBuildSettings.ManifestFileName));
            yield return manifestReq.SendWebRequest();

            if(manifestReq.isHttpError || manifestReq.isNetworkError)
            {
                result.Done(false);
                yield break;
            }

            var remoteManifestJson = manifestReq.downloadHandler.text;

            if (!AssetbundleBuildManifest.TryParse(remoteManifestJson, out var remoteManifest))
            {
                result.Done(false);
                yield break;
            }

            result.SetIndexLength(remoteManifest.BundleInfos.Count);
            bool bundleReplaced = false; //bundle has been replaced

            for (int i = 0; i < remoteManifest.BundleInfos.Count; i++)
            {
                result.SetCurrentIndex(i);
                var bundleInfo = remoteManifest.BundleInfos[i];
                var localBundle = s_LocalBundles.TryGetValue(bundleInfo.BundleName, out var localHash) && localHash == bundleInfo.Hash;
                var isCached = Caching.IsVersionCached(bundleInfo.AsCached);

                var loadURL = localBundle ? Path.Combine(LocalURL, bundleInfo.BundleName) : Path.Combine(RemoteURL, bundleInfo.BundleName);
                if (LogMessages) Debug.Log($"Loading Bundle Name : {bundleInfo.BundleName}, loadURL {loadURL}, isLocalBundle : {localBundle}, isCached {isCached}");
                LoadedBundle previousBundle;

                if (s_AssetBundles.TryGetValue(bundleInfo.BundleName, out previousBundle) && previousBundle.Hash == bundleInfo.Hash)
                {
                    if (LogMessages) Debug.Log($"Loading Bundle Name : {bundleInfo.BundleName} Complete - load skipped");
                }
                else
                {
                    var bundleReq = localBundle ? UnityWebRequestAssetBundle.GetAssetBundle(loadURL) : UnityWebRequestAssetBundle.GetAssetBundle(loadURL, bundleInfo.AsCached);
                    var operation = bundleReq.SendWebRequest();
                    while (!bundleReq.isDone)
                    {
                        result.SetProgress(operation.progress);
                        yield return null;
                    }

                    if (bundleReq.isNetworkError || bundleReq.isHttpError)
                    {
                        result.Done(false);
                        yield break;
                    }

                    if (s_AssetBundles.TryGetValue(bundleInfo.BundleName, out previousBundle))
                    {
                        bundleReplaced = true;
                        previousBundle.Bundle.Unload(hardUnload);
                        if (previousBundle.RequestForReload != null) 
                            previousBundle.RequestForReload.Dispose(); //dispose reload bundle
                        s_AssetBundles.Remove(bundleInfo.BundleName);
                    }

                    var loadedBundle = new LoadedBundle(bundleInfo, loadURL, DownloadHandlerAssetBundle.GetContent(bundleReq), localBundle);
                    s_AssetBundles.Add(bundleInfo.BundleName, loadedBundle);
                    CollectSceneNames(loadedBundle);
                    if (LogMessages) Debug.Log($"Loading Bundle Name : {bundleInfo.BundleName} Complete");
                    bundleReq.Dispose();
                }

                Caching.MarkAsUsed(bundleInfo.AsCached);
            }

            var timeTook = Time.realtimeSinceStartup - startTime;
            if (LogMessages) Debug.Log($"CacheUsed Before Cleanup : {Caching.defaultCache.spaceOccupied} bytes");
            Caching.ClearCache((int)timeTook + 600);
            if (LogMessages) Debug.Log($"CacheUsed After CleanUp : {Caching.defaultCache.spaceOccupied} bytes");

            GlobalBundleHash = remoteManifest.GlobalHash.ToString();
            PlayerPrefs.SetString("CachedManifest", remoteManifestJson);
            PlayerPrefs.Save();
            result.Result = bundleReplaced;
            result.Done(true);
        }
    }

}
using System.Threading;
#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;
#endif

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Runtime Bluetooth permissions. Android 12+ (API 31) needs BLUETOOTH_SCAN, BLUETOOTH_ADVERTISE and
    /// BLUETOOTH_CONNECT; older Android needs ACCESS_FINE_LOCATION to scan. iOS shows its own prompt the first
    /// time CoreBluetooth is used (the native bridge reports a denial as Failed/PermissionDenied), so on iOS and
    /// in the Editor this class reports everything as granted.
    /// </summary>
    public static class BlePermissions
    {
        public enum Status : byte { Granted, Pending, Denied }

        private static int _requested;
        private static int _answered;
        private static int _denied;
        private static bool _inFlight;

        public const string Scan = "android.permission.BLUETOOTH_SCAN";
        public const string Advertise = "android.permission.BLUETOOTH_ADVERTISE";
        public const string Connect = "android.permission.BLUETOOTH_CONNECT";
        public const string FineLocation = "android.permission.ACCESS_FINE_LOCATION";

        /// <summary>The permissions this OS version needs for both hosting and joining.</summary>
        public static string[] Required()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            return AndroidSdk() >= 31 ? new[] { Scan, Advertise, Connect } : new[] { FineLocation };
#else
            return new string[0];
#endif
        }

        public static bool HasAll()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string[] perms = Required();
            for (int i = 0; i < perms.Length; i++)
            {
                if (!Permission.HasUserAuthorizedPermission(perms[i])) return false;
            }
            return true;
#else
            return true;
#endif
        }

        /// <summary>Shows the system dialog for the missing permissions (no-op if granted or already asking).</summary>
        public static void Request()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_inFlight) return;
            string[] perms = Required();
            var missing = new List<string>();
            for (int i = 0; i < perms.Length; i++)
            {
                if (!Permission.HasUserAuthorizedPermission(perms[i])) missing.Add(perms[i]);
            }
            if (missing.Count == 0) return;
            _inFlight = true;
            Interlocked.Exchange(ref _requested, missing.Count);
            Interlocked.Exchange(ref _answered, 0);
            Interlocked.Exchange(ref _denied, 0);
            // Callbacks may arrive on the Android UI thread: only touch atomics in them.
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => Interlocked.Increment(ref _answered);
            callbacks.PermissionDenied += _ =>
            {
                Interlocked.Increment(ref _denied);
                Interlocked.Increment(ref _answered);
            };
            Permission.RequestUserPermissions(missing.ToArray(), callbacks);
#endif
        }

        /// <summary>Call from the main thread while waiting (BleTransport.Poll does).</summary>
        public static Status Check()
        {
            if (HasAll())
            {
                _inFlight = false;
                return Status.Granted;
            }
            if (!_inFlight) return Status.Denied;
            int requested = Volatile.Read(ref _requested);
            if (Volatile.Read(ref _answered) >= requested && requested > 0)
            {
                _inFlight = false;
                return Volatile.Read(ref _denied) > 0 ? Status.Denied : (HasAll() ? Status.Granted : Status.Denied);
            }
            return Status.Pending;
        }

        /// <summary>Forget an unanswered request (e.g. the dialog was dismissed and timed out).</summary>
        public static void Abandon() => _inFlight = false;

        public static bool IsRequesting => _inFlight;

#if UNITY_ANDROID && !UNITY_EDITOR
        private static int _sdk = -1;

        public static int AndroidSdk()
        {
            if (_sdk < 0)
            {
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                {
                    _sdk = version.GetStatic<int>("SDK_INT");
                }
            }
            return _sdk;
        }
#endif
    }
}

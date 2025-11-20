using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace CarSpec
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            EnsureBluetoothPermissions();
        }

        void EnsureBluetoothPermissions()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.S)
                return; // old Android, handled by classic BLUETOOTH + location

            var toRequest = new List<string>();

            if (CheckSelfPermission(Manifest.Permission.BluetoothScan) != Permission.Granted)
                toRequest.Add(Manifest.Permission.BluetoothScan);

            if (CheckSelfPermission(Manifest.Permission.BluetoothConnect) != Permission.Granted)
                toRequest.Add(Manifest.Permission.BluetoothConnect);

            if (toRequest.Count > 0)
            {
                RequestPermissions(toRequest.ToArray(), requestCode: 1001);
            }
        }
    }
}

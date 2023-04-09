using JitHub.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Store;
using Windows.Storage;

namespace JitHub.Services
{
    public class FeatureService : ObservableObject
    {
        private bool _proLicense;
        private const string PRO_LICENSE = "PRO_LICENSE";
        public bool ProLicense
        {
            get => _proLicense;
            set => SetProperty(ref _proLicense, value);
        }

        public async Task SetLicenseStatus()
        {
            LicenseInformation licenseInformation;
#if DEBUG
            await ConfigureSimulatorAsync();
            licenseInformation = CurrentAppSimulator.LicenseInformation;
#else
            licenseInformation = CurrentApp.LicenseInformation;
#endif
            ProLicense = licenseInformation.ProductLicenses[PRO_LICENSE].IsActive;
        }

        public async Task ConfigureSimulatorAsync()
        {
            StorageFile proxyFile = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/Data/in-app-purchase.xml"));
            await CurrentAppSimulator.ReloadSimulatorAsync(proxyFile);
        }

        public async Task<FeaturePurchaseState> BuyProFeature()
        {
            LicenseInformation licenseInformation;
#if DEBUG
            licenseInformation = CurrentAppSimulator.LicenseInformation;
#else
            licenseInformation = CurrentApp.LicenseInformation;
#endif
            if (!licenseInformation.ProductLicenses[PRO_LICENSE].IsActive)
            {
                try
                {
#if DEBUG
                    await CurrentAppSimulator.RequestProductPurchaseAsync(PRO_LICENSE);
#else
                    await CurrentApp.RequestProductPurchaseAsync(PRO_LICENSE);
#endif
                    ProLicense = licenseInformation.ProductLicenses[PRO_LICENSE].IsActive;
                    return ProLicense ? FeaturePurchaseState.Success : FeaturePurchaseState.Cancel;
                }
                catch (Exception)
                {
                    return FeaturePurchaseState.Failure;
                }
            }
            else
            {
                return FeaturePurchaseState.AlreadyOwn;
            }
        }
    }
}

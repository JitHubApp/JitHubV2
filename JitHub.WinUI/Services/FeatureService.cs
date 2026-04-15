using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models;
using Microsoft.UI.Xaml;
using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Services.Store;
using WinRT.Interop;

namespace JitHub.Services;

public sealed class FeatureService : ObservableObject
{
    private const string DebugProLicenseKey = "DEBUG_PRO_LICENSE_ACTIVE";
    private const string ProLicenseStoreIdKey = "PRO_LICENSE_STORE_ID";
    private const string DurableProductType = "Durable";
    private const string ProLicenseProductId = "PRO_LICENSE";

    private readonly ISettingService _settingService;
    private readonly INotificationService _notificationService;
    private readonly StoreContext _storeContext;
    private bool _proLicense;

    public FeatureService(Window window, ISettingService settingService, INotificationService notificationService)
    {
        _settingService = settingService;
        _notificationService = notificationService;
        _storeContext = StoreContext.GetDefault();
        InitializeWithWindow.Initialize(_storeContext, WindowNative.GetWindowHandle(window));
    }

    public bool ProLicense
    {
        get => _proLicense;
        private set => SetProperty(ref _proLicense, value);
    }

    public async Task SetLicenseStatus()
    {
#if DEBUG
        await Task.CompletedTask;
        ProLicense = _settingService.Get<bool>(DebugProLicenseKey);
#else
        StoreAppLicense appLicense;
        try
        {
            appLicense = await _storeContext.GetAppLicenseAsync();
        }
        catch (Exception ex)
        {
            _notificationService.Push($"JitHub could not refresh Microsoft Store licensing: {ex.Message}");
            return;
        }

        if (TryGetCachedLicenseStatus(appLicense, out bool cachedLicenseStatus))
        {
            ProLicense = cachedLicenseStatus;
            return;
        }

        StoreProduct? proProduct = await GetProProductAsync();
        if (proProduct is null)
        {
            return;
        }

        CacheProStoreId(proProduct.StoreId);
        ProLicense = appLicense.AddOnLicenses.TryGetValue(proProduct.StoreId, out StoreLicense? addOnLicense) &&
            addOnLicense.IsActive;
#endif
    }

    public async Task<FeaturePurchaseState> BuyProFeature()
    {
#if DEBUG
        if (ProLicense)
        {
            return FeaturePurchaseState.AlreadyOwn;
        }

        _settingService.Save(DebugProLicenseKey, true);
        ProLicense = true;
        await Task.CompletedTask;
        return FeaturePurchaseState.Success;
#else
        await SetLicenseStatus();
        if (ProLicense)
        {
            return FeaturePurchaseState.AlreadyOwn;
        }

        StoreProduct? proProduct = await GetProProductAsync();
        if (proProduct is null)
        {
            return FeaturePurchaseState.Failure;
        }

        CacheProStoreId(proProduct.StoreId);
        StorePurchaseResult purchaseResult;
        try
        {
            purchaseResult = await proProduct.RequestPurchaseAsync();
        }
        catch (Exception ex)
        {
            _notificationService.Push($"JitHub could not complete the Microsoft Store purchase: {ex.Message}");
            return FeaturePurchaseState.Failure;
        }

        switch (purchaseResult.Status)
        {
            case StorePurchaseStatus.Succeeded:
                await SetLicenseStatus();
                return ProLicense ? FeaturePurchaseState.Success : FeaturePurchaseState.Failure;
            case StorePurchaseStatus.AlreadyPurchased:
                ProLicense = true;
                return FeaturePurchaseState.AlreadyOwn;
            case StorePurchaseStatus.NotPurchased:
                return FeaturePurchaseState.Cancel;
            case StorePurchaseStatus.NetworkError:
            case StorePurchaseStatus.ServerError:
            default:
                return FeaturePurchaseState.Failure;
        }
#endif
    }

    private async Task<StoreProduct?> GetProProductAsync()
    {
        StoreProductQueryResult queryResult;
        try
        {
            queryResult = await _storeContext.GetAssociatedStoreProductsAsync(new[] { DurableProductType });
        }
        catch (Exception ex)
        {
            _notificationService.Push($"JitHub could not query Microsoft Store products: {ex.Message}");
            return null;
        }

        if (queryResult.ExtendedError is not null)
        {
            _notificationService.Push(queryResult.ExtendedError.Message);
            return null;
        }

        return queryResult.Products.Values.FirstOrDefault(product =>
            string.Equals(product.InAppOfferToken, ProLicenseProductId, StringComparison.Ordinal));
    }

    private void CacheProStoreId(string storeId)
    {
        if (!string.IsNullOrWhiteSpace(storeId))
        {
            _settingService.Save(ProLicenseStoreIdKey, storeId);
        }
    }

    private bool TryGetCachedLicenseStatus(StoreAppLicense appLicense, out bool isActive)
    {
        string? cachedStoreId = _settingService.Get<string>(ProLicenseStoreIdKey);
        if (!string.IsNullOrWhiteSpace(cachedStoreId) &&
            appLicense.AddOnLicenses.TryGetValue(cachedStoreId, out StoreLicense? cachedLicense))
        {
            isActive = cachedLicense.IsActive;
            return true;
        }

        isActive = false;
        return false;
    }
}

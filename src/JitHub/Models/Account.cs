using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Models
{
    public class Account : ObservableObject
    {
        private int _id;
        private string _login;
        private string _avatarUrl;
        private bool _isLoggedIn;
        private bool _isActive;
        public int Id { get => _id; set => SetProperty(ref _id, value); }
        public string Login { get => _login; set => SetProperty(ref _login, value); }
        public string AvatarUrl { get => _avatarUrl; set => SetProperty(ref _avatarUrl, value); }
        public bool IsLoggedIn { get => _isLoggedIn; set => SetProperty(ref _isLoggedIn, value); }
        public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    }
}

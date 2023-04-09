using JitHub.Models.Base;
using CommunityToolkit.Mvvm.ComponentModel;
using Octokit;
using System.Collections.Generic;

namespace JitHub.Models
{
    public class SettingsDisplayItem
    {
        public string DisplayText { get; set; }
        public object Model { get; set; }

        public SettingsDisplayItem(string text, object model)
        {
            DisplayText = text;
            Model = model;
        }
    }

    public interface SettingsItemType
    {
        string GetItemType();
    }

    public class SettingsItem<T> : RepoSelectableItemModel<T>, SettingsItemType
    {
        private string _label;
        private string _type;
        private ICollection<SettingsDisplayItem> _items;
        public string Label
        {
            get => _label;
            set => SetProperty(ref _label, value);
        }
        public string Type
        {
            get => _type;
            set => SetProperty(ref _type, value);
        }
        public ICollection<SettingsDisplayItem> Items
        {
            get => _items;
            set => SetProperty(ref _items, value);
        }

        public SettingsItem(Repository repo, T model, string label, string type)
        {
            Repository = repo;
            Model = model;
            Label = label;
            Type = type;
        }

        public string GetItemType()
        {
            return Type;
        }
    }
}

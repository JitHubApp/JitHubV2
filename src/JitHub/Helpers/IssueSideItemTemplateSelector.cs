﻿using JitHub.Models;
using JitHub.Models.Base;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace JitHub.Helpers
{
    public class IssueSideItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }
        public DataTemplate LabelTemplate { get; set; }
        protected override DataTemplate SelectTemplateCore(object item)
        {
            switch(((SelectableItem)item).Type)
            {
                case "User":
                    return UserTemplate;
                case "Label":
                    return LabelTemplate;
                default:
                    return null;
            }
        }
    }
}

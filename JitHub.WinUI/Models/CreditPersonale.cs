using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace JitHub.Models
{
    [WinRT.GeneratedBindableCustomProperty]
    public partial class CreditPersonale
    {
        public ImageSource ImageSource { get; set; } = new BitmapImage(new Uri("ms-appx:///Assets/Octocat.png"));
        public List<PersonalLink> Links { get; set; } = new();
        public string PersonaleName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public Brush BackgroundBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        public CreditPersonale(string url, string name, string role, string description, Color color, IEnumerable<PersonalLink> links)
        {
            try
            {
                ImageSource = new BitmapImage(new Uri(url));
            }
            catch (Exception)
            {
                ImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Octocat.png"));
            }
            PersonaleName = name;
            Role = role;
            Description = description;
            BackgroundBrush = new SolidColorBrush(color);
            Links = links.ToList();
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    public partial class PersonalLink
    {
        public string Link { get; set; } = string.Empty;
        public ImageSource LogoSource { get; set; } = new BitmapImage(new Uri("ms-appx:///Assets/Octocat.png"));
        public const string LinkedInLogo = "ms-appx:///Assets/LinkLogos/LinkedIn.png";
        public const string TwitterLogo = "ms-appx:///Assets/LinkLogos/Twitter.png";
        public const string GitHubLogo = "ms-appx:///Assets/LinkLogos/GitHub.png";
        public const string GoogleScholarLogo = "ms-appx:///Assets/LinkLogos/GoogleScholar.png";

        public PersonalLink(string link, string logoPath)
        {
            try
            {
                LogoSource = new BitmapImage(new Uri(logoPath));
            }
            catch (Exception)
            {
                LogoSource = new BitmapImage(new Uri("ms-appx:///Assets/Octocat.png"));
            }
            Link = link;
        }
    }
}


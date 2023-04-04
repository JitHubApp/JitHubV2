using System;
using System.Collections;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace JitHub.Models
{
    public class CreditPersonale
    {
        public ImageSource ImageSource { get; set; }
        public ICollection<PersonalLink> Links { get; set; }
        public string PersonaleName { get; set; }
        public string Role { get; set; }
        public string Description { get; set; }
        public Brush BackgroundBrush { get; set; }

        public CreditPersonale(string url, string name, string role, string description, Color color, ICollection<PersonalLink> links)
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
            Links = links;
        }
    }

    public class PersonalLink
    {
        public string Link { get; set; }
        public ImageSource LogoSource { get; set; }
        public static string LinkedInLogo = "ms-appx:///Assets/LinkedInLogo.png";
        public static string TwitterLogo = "ms-appx:///Assets/TwitterLogo.png";

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

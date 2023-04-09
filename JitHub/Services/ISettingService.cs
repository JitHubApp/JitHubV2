using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace JitHub.Services
{
    public interface ISettingService
    {
        void Save<T>(string key, T value);
        T Get<T>(string key);
    }
}

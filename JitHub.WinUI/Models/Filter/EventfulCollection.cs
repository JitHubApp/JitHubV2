using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JitHub.WinUI.Models.Filter
{
    public class EventfulCollection<T>
    {
        public Action<T>? Action;
        public ObservableCollection<T> Instance { get; set; }

        public EventfulCollection()
        {
            Instance = new ObservableCollection<T>();
        }

        public void Add(T item)
        {
            Instance.Add(item);
            Action?.Invoke(item);
        }
    }
}

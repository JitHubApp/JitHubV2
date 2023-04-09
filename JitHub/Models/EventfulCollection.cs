using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JitHub.Models
{
    public class EventfulCollection<T>
    {
        public Action<T> Action;
        public ICollection<T> Instance { get; set; }

        public EventfulCollection()
        {
            Instance = new ObservableCollection<T>();
        }

        public void Add(T item)
        {
            Instance.Add(item);
            if (Action != null)
                Action.Invoke(item);
        }
    }
}

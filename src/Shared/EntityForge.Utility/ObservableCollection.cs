//using System;
//using System.Collections.Generic;
//using System.Collections.ObjectModel;
//using System.Collections.Specialized;
//using System.ComponentModel;
//using System.Linq;
//using System.Text;
//using System.Threading;
//using System.Threading.Tasks;

//namespace EntityForge.Utility
//{
//    /// <summary>
//    /// An observable collection that validates the current synchronization context
//    /// Unlike the standard WPF implementation, this doesn't rely on the (UI) Dispatcher
//    /// </summary>
//    public class ObservableCollection<T> : Collection<T>, INotifyCollectionChanged, INotifyPropertyChanged
//    {
//        private readonly SynchronizationContext _syncContext;

//        public event NotifyCollectionChangedEventHandler CollectionChanged;
//        public event PropertyChangedEventHandler PropertyChanged;

//        public ObservableCollection()
//        {
//            _syncContext = SynchronizationContext.Current;
//        }

//        protected void CheckAccess()
//        {
//            if (_syncContext != SynchronizationContext.Current) throw new InvalidOperationException("Synchronization context has changed");
//        }

//        protected void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
//        {
//            if (CollectionChanged != null) CollectionChanged(this, e);
//        }

//        protected override void ClearItems()
//        {
//            CheckAccess();

//            base.ClearItems();

//            // Indicate a big change has occurred
//            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
//        }

//        protected override void InsertItem(int index, T item)
//        {
//            CheckAccess();

//            base.InsertItem(index, item);

//            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
//        }

//        protected override void RemoveItem(int index)
//        {
//            CheckAccess();

//            base.RemoveItem(index);

//            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, index));
//        }
//    }
//}

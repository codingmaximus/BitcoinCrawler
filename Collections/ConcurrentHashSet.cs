using System.Collections;
using System.Collections.Concurrent;

namespace BitcoinCrawlerStats
{
    // Thread-safe HashSet
    public class ConcurrentHashSet<T> : ICollection<T> where T : class
    {
        private readonly ConcurrentDictionary<T, byte> _dict = new ConcurrentDictionary<T, byte>();

        public bool Add(T item)
        {
            return _dict.TryAdd(item, 0);
        }

        public bool Contains(T item) => _dict.ContainsKey(item);

        public IEnumerator<T> GetEnumerator()
        {
            return _dict.Keys.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        void ICollection<T>.Add(T item)
        {
            this.Add(item);
        }

        public void Clear()
        {
            _dict.Clear();
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            _dict.Keys.CopyTo(array, arrayIndex);
        }

        public bool Remove(T item)
        {
            return _dict.Remove(item, out _);
        }

        public int Count => _dict.Count;

        public bool IsReadOnly => false;

        public T this[int index] { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        /*
        public List<T> ToList => _dict.Keys.ToList();
        */
    }
}

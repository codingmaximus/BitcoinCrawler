using System.Collections.Concurrent;

namespace BitcoinCrawlerStats
{
    public class FixedFifoQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue = new();
        private readonly int _maxCapacity;

        public FixedFifoQueue(int maxCapacity = 10)
        {
            if (maxCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(maxCapacity), "Must be at least 1");

            _maxCapacity = maxCapacity;
        }

        public int Count => _queue.Count;
        public int Capacity => _maxCapacity;
        public bool IsFull => Count >= _maxCapacity;
        public bool IsEmpty => Count == 0;

        /// <summary>
        /// Adds an item to the end of the queue.
        /// If the queue is already full, the oldest item is automatically removed.
        /// </summary>
        public void Add(T item)
        {
            if (item == null && !typeof(T).IsValueType)
                throw new ArgumentNullException(nameof(item));

            if (_queue.Count >= _maxCapacity)
            {
                //_queue.Dequeue();           // remove oldest
                _queue.TryDequeue(out _);
            }

            _queue.Enqueue(item);
        }

        /// <summary>
        /// Attempts to add an item only if there is space.
        /// Returns true if the item was added, false if the queue was full.
        /// </summary>
        public bool TryAdd(T item)
        {
            if (IsFull) return false;
            Add(item);
            return true;
        }

        public T Remove()
        {
            if (_queue.Count == 0)
                throw new InvalidOperationException("Queue is empty");

            //return _queue.Dequeue();
            T ret;
            if (!_queue.TryDequeue(out ret!))
                return default(T)!;
            return ret;
        }

        public bool TryRemove(out T result)
        {
            if (_queue.Count == 0)
            {
                result = default!;
                return false;
            }

            //result = _queue.Dequeue();
            if (!_queue.TryDequeue(out result!))
                return false;
            return true;
        }

        public T Peek()
        {
            if (_queue.Count == 0)
                throw new InvalidOperationException("Queue is empty");

            //return _queue.Peek();
            T ret;
            if (!_queue.TryPeek(out ret!))
                return default(T)!;
            return ret;
        }

        public void Clear() => _queue.Clear();

        // For debugging / inspection
        public T[] ToArray() => _queue.ToArray();
    }
}

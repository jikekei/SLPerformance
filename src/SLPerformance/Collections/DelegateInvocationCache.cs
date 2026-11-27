using System;
using System.Runtime.CompilerServices;

namespace SLPerformance.Collections;

/// <summary>
/// Zero-allocation invocation list cache keyed by immutable Delegate reference via ConditionalWeakTable.
/// Old multicast delegates are collected automatically when handlers are added or removed.
/// </summary>
public static class DelegateInvocationCache
{
    private static readonly ConditionalWeakTable<Delegate, Delegate[]> Cache = new ConditionalWeakTable<Delegate, Delegate[]>();
    private static readonly ConditionalWeakTable<Delegate, Delegate[]>.CreateValueCallback CreateCallback = CreateInvocationList;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Delegate[] GetInvocationListCached(Delegate handler)
    {
        if (handler == null)
        {
            return Array.Empty<Delegate>();
        }

        return Cache.GetValue(handler, CreateCallback);
    }

    private static Delegate[] CreateInvocationList(Delegate handler)
    {
        return handler.GetInvocationList();
    }
}

/// <summary>
/// Dense array-backed map from uint keys (e.g. netId) to values with O(1) swap-remove and zero-alloc iteration.
/// </summary>
public sealed class CompactUintMap<TValue>
{
    private uint[] _keys;
    private TValue[] _values;
    private int _count;

    public int Count => _count;

    public CompactUintMap(int initialCapacity = 64)
    {
        int cap = initialCapacity < 8 ? 8 : initialCapacity;
        _keys = new uint[cap];
        _values = new TValue[cap];
    }

    public bool TryGetValue(uint key, out TValue value)
    {
        uint[] keys = _keys;
        int count = _count;
        for (int i = 0; i < count; i++)
        {
            if (keys[i] == key)
            {
                value = _values[i];
                return true;
            }
        }

        value = default;
        return false;
    }

    public void Set(uint key, in TValue value)
    {
        uint[] keys = _keys;
        int count = _count;
        for (int i = 0; i < count; i++)
        {
            if (keys[i] == key)
            {
                _values[i] = value;
                return;
            }
        }

        if (count == keys.Length)
        {
            int newSize = keys.Length * 2;
            Array.Resize(ref _keys, newSize);
            Array.Resize(ref _values, newSize);
        }

        _keys[count] = key;
        _values[count] = value;
        _count = count + 1;
    }

    public bool Remove(uint key)
    {
        uint[] keys = _keys;
        int count = _count;
        for (int i = 0; i < count; i++)
        {
            if (keys[i] == key)
            {
                int last = count - 1;
                if (i != last)
                {
                    keys[i] = keys[last];
                    _values[i] = _values[last];
                }

                _values[last] = default;
                _count = last;
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        if (_count > 0)
        {
            Array.Clear(_values, 0, _count);
            _count = 0;
        }
    }
}

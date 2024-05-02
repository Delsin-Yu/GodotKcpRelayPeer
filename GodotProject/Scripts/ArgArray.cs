using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Godot;

namespace Helpers;

public static class ArgArray
{

    private static class Backing<T>
    {
        private static readonly Dictionary<uint, T[]> _dictionary = [];

        public static T[] Get(uint amount)
        {
            ref var array = ref CollectionsMarshal.GetValueRefOrAddDefault(_dictionary, amount, out var exists);
            if(!exists) array = new T[amount];
            return array;
        }
    }

    public static Variant[] Get(ReadOnlySpan<Variant> values)
    {
        var array = Backing<Variant>.Get((uint)values.Length);
        for (var index = 0; index < values.Length; index++)
        {
            array[index] = values[index];
        }

        return array;
    }
}
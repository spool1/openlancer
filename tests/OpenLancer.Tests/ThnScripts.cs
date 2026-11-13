using System;
using System.Collections.Generic;
using System.Text;
using LibreLancer;
using LibreLancer.Thorn;
using OpenLancer.Data.Thn;
using OpenLancer.Runtime.Thn;

namespace OpenLancer.Tests;

/// <summary>
/// Builds THN trees in code and evaluates THN text. The repo holds no <c>.thn</c> files, so the tests make their
/// input here.
/// </summary>
internal static class ThnScripts
{
    // The native compiler loads from the x64 folder of the test output.
    static ThnScripts() => AppHandler.ConsoleInit(silent: true);

    public static ThornEvaluator Evaluator => ThornEvaluator.Instance;

    /// <summary>The engine VM, with a count of its runs, so that a test can show that a text does not run.</summary>
    public sealed class CountingEvaluator : IThnEvaluator
    {
        public int Runs { get; private set; }

        public IReadOnlyDictionary<string, ThnSymbolInfo> Symbols => Evaluator.Symbols;

        public ThnTable Evaluate(ReadOnlySpan<byte> script, string name)
        {
            Runs++;
            return Evaluator.Evaluate(script, name);
        }
    }

    /// <summary>Evaluates Lua text with the engine VM.</summary>
    public static ThnTable Evaluate(string text) => Evaluator.Evaluate(Encoding.ASCII.GetBytes(text), "test.thn");

    /// <summary>Compiles Lua text to bytecode with the native compiler. The compiler loads only after the static
    /// constructor of this class, so a test compiles through this method.</summary>
    public static byte[] Compile(string text) => ThornCompiler.Compile(text, "test.thn");

    public static ThnNumber N(float value) => new(value);

    public static ThnText T(string value) => new(value);

    public static ThnSymbol S(string name) => new(name);

    public static ThnField F(string key, ThnValue value) => new(key, value);

    public static ThnTable List(params ThnValue[] items) => ThnTable.FromList(items);

    public static ThnTable Record(params ThnField[] fields) => ThnTable.FromRecord(fields);
}

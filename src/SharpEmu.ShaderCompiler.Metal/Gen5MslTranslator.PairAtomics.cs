// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Metal;

public static partial class Gen5MslTranslator
{
    private sealed partial class CompilationContext
    {
        private const string PairLocksExpression = $"{ResourcesName}.{Gen5MslArgumentLayout.PairLocksName}";
        private const string PairStepReportExpression = $"{ResourcesName}.{Gen5MslArgumentLayout.StepReportName}";

        /// <summary>A guest dword-pair atomic (an *_X2 atomic) under the pair's sequence lock
        /// (Templates/pair_atomic_prelude.msl), so a GLC LOAD_DWORDX2 never sees one dword
        /// from before it and one from after. The body runs each dword's 32-bit atomic; it is
        /// emitted inside the branch that took the lock, which is what keeps lanes of one
        /// SIMD-group that contend for a slot from waiting on each other.</summary>
        private void EmitPairLocked(string firstWord, Action body)
        {
            _usesPairLocks = true;
            var lockWord = Temp("device atomic_uint*", $"sharpemu_pair_lock({PairLocksExpression}, {firstWord})");
            var sequence = Temp("uint", "0u");
            var tries = Temp("uint", "0u");
            var done = Temp("bool", "false");
            Line($"while (!{done})");
            Line("{");
            _indent++;
            Line($"if (sharpemu_pair_try_lock({lockWord}, {sequence}, {tries}, {PairStepReportExpression}))");
            Line("{");
            _indent++;
            body();
            Line($"sharpemu_pair_unlock({lockWord}, {sequence});");
            Line($"{done} = true;");
            _indent--;
            Line("}");
            _indent--;
            Line("}");
        }

        /// <summary>A GLC LOAD_DWORDX2 read as one snapshot (see <see cref="EmitPairLocked"/>)
        /// into two destination registers. The helper call is the whole read, so it runs
        /// only for lanes EXEC has set unless the caller already guards on it.</summary>
        private void StorePairLoad(uint destination, string pairExpression, bool guardWithExec = true)
        {
            _usesPairLocks = true;
            var pair = Temp("uint2", "uint2(0u)");
            Line(guardWithExec && !_execKnownFull
                ? $"if (exec) {{ {pair} = {pairExpression}; }}"
                : $"{pair} = {pairExpression};");
            StoreVector(destination, $"{pair}.x", guardWithExec);
            StoreVector(destination + 1, $"{pair}.y", guardWithExec);
        }

        private static string PairLockArguments => $"{PairLocksExpression}, {PairStepReportExpression}";
    }
}

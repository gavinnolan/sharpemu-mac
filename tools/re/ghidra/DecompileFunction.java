// Decompiles the function(s) containing the given addresses to C-like text.
// Args: <outfile|-> <addr> [addr ...]   addr = hex; values below the image base are treated as app+X
// offsets (ELF vaddrs), absolute runtime addresses (0x8xxxxxxxx) are accepted as-is.
// Output goes to outfile ('-' = the script log). Normally driven through ghidra/run.sh.
// @category SharpEmu.RE
import java.io.PrintWriter;

import ghidra.app.decompiler.DecompInterface;
import ghidra.app.decompiler.DecompileResults;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DecompileFunction extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("usage: DecompileFunction.java <outfile|-> <addr> [addr ...]");
            return;
        }
        long base = currentProgram.getImageBase().getOffset();
        DecompInterface ifc = new DecompInterface();
        ifc.openProgram(currentProgram);
        StringBuilder out = new StringBuilder();
        for (int i = 1; i < args.length; i++) {
            long v = Long.decode(args[i]);
            Address a = toAddr(v < base ? base + v : v);
            Function f = getFunctionContaining(a);
            if (f == null) {
                out.append("// no function contains ").append(a).append("\n");
                continue;
            }
            DecompileResults res = ifc.decompileFunction(f, 120, monitor);
            out.append("// ").append(f.getName()).append(" @ ").append(f.getEntryPoint())
               .append(" (app+0x").append(Long.toHexString(f.getEntryPoint().getOffset() - base)).append(")\n");
            if (res.decompileCompleted()) {
                out.append(res.getDecompiledFunction().getC()).append("\n");
            } else {
                out.append("// decompile failed: ").append(res.getErrorMessage()).append("\n");
            }
        }
        if (args[0].equals("-")) {
            println(out.toString());
        } else {
            try (PrintWriter w = new PrintWriter(args[0])) {
                w.print(out);
            }
        }
    }
}

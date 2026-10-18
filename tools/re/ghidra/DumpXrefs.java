// Lists every reference to the given address(es): from-address, app+X offset, reference type, and the
// containing function. Args: <outfile|-> <addr> [addr ...] (same address forms as DecompileFunction).
// Normally driven through ghidra/run.sh.
// @category SharpEmu.RE
import java.io.PrintWriter;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceIterator;

public class DumpXrefs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 2) {
            printerr("usage: DumpXrefs.java <outfile|-> <addr> [addr ...]");
            return;
        }
        long base = currentProgram.getImageBase().getOffset();
        StringBuilder out = new StringBuilder();
        for (int i = 1; i < args.length; i++) {
            long v = Long.decode(args[i]);
            Address a = toAddr(v < base ? base + v : v);
            out.append("# xrefs to ").append(a).append(" (app+0x").append(Long.toHexString(a.getOffset() - base)).append(")\n");
            ReferenceIterator it = currentProgram.getReferenceManager().getReferencesTo(a);
            int n = 0;
            while (it.hasNext()) {
                Reference ref = it.next();
                Address from = ref.getFromAddress();
                Function f = getFunctionContaining(from);
                out.append(from).append("\tapp+0x").append(Long.toHexString(from.getOffset() - base))
                   .append('\t').append(ref.getReferenceType())
                   .append('\t').append(f == null ? "-" : f.getName() + "@app+0x" + Long.toHexString(f.getEntryPoint().getOffset() - base))
                   .append('\n');
                n++;
            }
            out.append("# ").append(n).append(" references\n");
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

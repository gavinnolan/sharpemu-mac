// Names the PLT stubs and GOT slots of a flattened PS4/PS5 ELF from nidmap.py's imports.tsv.
// Run as a -preScript (before auto-analysis) so the analyzers see the names (non-returning
// functions such as __stack_chk_fail/abort are marked here too).
// Args: <imports.tsv>
// Address convention: tsv addresses are ELF vaddrs (app+X); the program is loaded at image base
// 0x800000000, so the Ghidra address is base + vaddr.
// @category SharpEmu.RE
import java.io.BufferedReader;
import java.io.FileReader;
import java.util.Set;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.SourceType;

public class ApplyImports extends GhidraScript {
    private static final Set<String> NORETURN = Set.of(
        "__stack_chk_fail", "abort", "exit", "_exit", "__cxa_pure_virtual", "scePthreadExit", "pthread_exit",
        "longjmp", "_longjmp", "siglongjmp", "__assert", "__assert_fail", "_Exit", "quick_exit", "sceKernelExitProcess");

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("usage: ApplyImports.java <imports.tsv>");
            return;
        }
        long base = currentProgram.getImageBase().getOffset();
        int stubs = 0, slots = 0, unresolved = 0;
        try (BufferedReader r = new BufferedReader(new FileReader(args[0]))) {
            String line;
            while ((line = r.readLine()) != null) {
                if (line.isEmpty() || line.startsWith("#")) continue;
                String[] c = line.split("\t");
                String nid = c[3], name = c[4], lib = c[5];
                if (name.equals("?")) {
                    name = "sce_unk_" + nid.replaceAll("[^A-Za-z0-9]", "_");
                    unresolved++;
                }
                Address slot = toAddr(base + Long.decode(c[0]));
                label(slot, "got_" + name);
                slots++;
                if (!c[1].equals("-")) {
                    Address stub = toAddr(base + Long.decode(c[1]));
                    Function f = getFunctionAt(stub);
                    if (f == null) {
                        disassemble(stub);
                        f = createFunction(stub, null);
                    }
                    if (f == null) continue;
                    try {
                        f.setName(name, SourceType.IMPORTED);
                    } catch (Exception e) {
                        f.setName(name + "_" + Long.toHexString(stub.getOffset()), SourceType.IMPORTED);
                    }
                    f.setComment("import " + lib + "::" + name + " NID " + nid);
                    if (NORETURN.contains(name)) f.setNoReturn(true);
                    stubs++;
                }
            }
        }
        println("ApplyImports: " + stubs + " PLT stubs, " + slots + " GOT slots named (" + unresolved + " unresolved NIDs)");
    }

    private void label(Address a, String name) {
        try {
            createLabel(a, name, true, SourceType.IMPORTED);
        } catch (Exception e) {
            try {
                createLabel(a, name + "_" + Long.toHexString(a.getOffset()), true, SourceType.IMPORTED);
            } catch (Exception ignored) {
            }
        }
    }
}

// Marks every function start from the ELF's PT_GNU_EH_FRAME table (funcstarts.py output) as an entry
// point so auto-analysis disassembles from them: PS5 ELFs have no section headers, so Ghidra's own
// .eh_frame analyzer never runs. Run as a -preScript.
// Args: <functions.txt>   (one ELF vaddr, hex, per line)
// @category SharpEmu.RE
import java.io.BufferedReader;
import java.io.FileReader;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;

public class SeedFunctions extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            printerr("usage: SeedFunctions.java <functions.txt>");
            return;
        }
        long base = currentProgram.getImageBase().getOffset();
        int n = 0;
        try (BufferedReader r = new BufferedReader(new FileReader(args[0]))) {
            String line;
            while ((line = r.readLine()) != null) {
                line = line.trim();
                if (line.isEmpty() || line.startsWith("#")) continue;
                Address a = toAddr(base + Long.decode(line));
                if (currentProgram.getMemory().contains(a)) {
                    currentProgram.getSymbolTable().addExternalEntryPoint(a);
                    n++;
                }
            }
        }
        println("SeedFunctions: " + n + " entry points");
    }
}

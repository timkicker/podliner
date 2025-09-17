using System;

static class TerminalUtil
{
    public static void ResetHard()
    {
        try
        {
            Console.Write(
                "\x1b[?1000l\x1b[?1002l\x1b[?1003l\x1b[?1006l\x1b[?1015l" + // mouse off
                "\x1b[?2004l" +                                           // bracketed paste off
                "\x1b[?25h"   +                                           // cursor on
                "\x1b[0m"     +                                           // sgr reset
                "\x1b[?1049l"                                            // leave alt screen
            );
            Console.Out.Flush();
            Console.Write("\x1bc"); // RIS
            Console.Out.Flush();
        }
        catch { }
    }
}
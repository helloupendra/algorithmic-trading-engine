' Launches a PowerShell script with no console window at all.
'
' Task Scheduler running powershell.exe directly flashes a console on screen
' every time it fires. -WindowStyle Hidden does not help: the window is created
' and then hidden, so it still blinks. wscript.exe has no console of its own, so
' anything it starts with intWindowStyle 0 never draws one.
'
' Usage (from a scheduled task):
'   wscript.exe "...\scripts\run-hidden.vbs" "...\scripts\auto-deploy.ps1"

Option Explicit

Dim shell, fso, scriptPath, command, i

If WScript.Arguments.Count < 1 Then
    WScript.Quit 2
End If

scriptPath = WScript.Arguments(0)

Set fso = CreateObject("Scripting.FileSystemObject")
If Not fso.FileExists(scriptPath) Then
    WScript.Quit 3
End If

command = "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File """ & scriptPath & """"

' Anything after the script path is passed straight through to it.
For i = 1 To WScript.Arguments.Count - 1
    command = command & " " & WScript.Arguments(i)
Next

Set shell = CreateObject("WScript.Shell")

' 0 = hidden window, True = wait for it to finish so the task's Last Result is
' the script's real exit code rather than "started something, then left".
WScript.Quit shell.Run(command, 0, True)

"""Run from project root with pywinauto installed. Uses an isolated preferences folder."""
import os, sys, tempfile, time, subprocess
from pathlib import Path
from pywinauto import Application

root = Path.cwd()
with tempfile.TemporaryDirectory() as profile:
    env=dict(os.environ, MYPLAMO_PROFILE=profile)
    proc=subprocess.Popen([str(root/"release/MyPLaMo/MyPLaMo.exe")],env=env)
    try:
        app=Application(backend="uia").connect(process=proc.pid,timeout=60)
        main=app.window(title_re="MyPLaMo.*ローカル翻訳")
        setup=main.child_window(title_re="MyPLaMo.*モデルと設定",control_type="Window")
        setup.wait("visible",timeout=60)
        setup.capture_as_image().save(root/"build/setup.png")
        # Use the recommended model on this machine.
        setup.descendants(control_type="Edit")[0].set_edit_text(str(root/"models"))
        setup.child_window(title="PLaMo Community License に同意する",control_type="CheckBox").toggle()
        setup.child_window(title="準備して使う",control_type="Button").invoke()
        main=app.window(title_re="MyPLaMo.*ローカル翻訳")
        setup.wait_not("visible",timeout=100)
        main.descendants(control_type="Edit")[0].set_edit_text("The sky is blue.")
        main.child_window(title="翻訳する",control_type="Button").invoke()
        output=main.descendants(control_type="Edit")[1]
        for i in range(120):
            result=output.get_value()
            if result: break
            time.sleep(.25)
        assert "青" in result,result
        main.capture_as_image().save(root/"build/translation.png")
        print("UI translated:",result,flush=True)
        main.child_window(title="⇄  入れ替え",control_type="Button").invoke()
        main.child_window(title="翻訳する",control_type="Button").invoke()
        for i in range(120):
            result=output.get_value()
            if "blue" in result.lower():break
            time.sleep(.25)
        assert "blue" in result.lower(),result
        print("UI reverse:",result,flush=True)
        import win32clipboard
        from pywinauto.keyboard import send_keys
        def clipboard_text(value=None):
            win32clipboard.OpenClipboard()
            try:
                if value is None:return win32clipboard.GetClipboardData(13)
                win32clipboard.EmptyClipboard()
                win32clipboard.SetClipboardText(value,13)
            finally:win32clipboard.CloseClipboard()
        main.child_window(title="⇄  入れ替え",control_type="Button").invoke()
        main.child_window(title="クリア",control_type="Button").invoke()
        old_text=clipboard_text() if win32clipboard.IsClipboardFormatAvailable(13) else None
        editor=subprocess.Popen(["powershell.exe","-NoProfile","-STA","-File",str(root/"tests/editor.ps1")],creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            window=Application(backend="uia").connect(process=editor.pid,timeout=15).window(title="MyPLaMo selection test")
            window.wait("visible",timeout=15)
            window.set_focus()
            send_keys("^a")
            clipboard_text("previous clipboard contents")
            send_keys("^%t")
            for i in range(100):
                if main.child_window(title="元のアプリに挿入",control_type="Button").exists(timeout=.1) and "青" in output.get_value():break
                time.sleep(.15)
            assert "青" in output.get_value()
            assert clipboard_text()=="previous clipboard contents"
            print("Hotkey selection and clipboard restoration passed",flush=True)
            import win32gui,win32con
            button=main.child_window(title="元のアプリに挿入",control_type="Button").wrapper_object()
            win32gui.PostMessage(button.handle,win32con.BM_CLICK,0,0)
            dialog=main.child_window(title="訳文の挿入",control_type="Window")
            dialog.wait("visible",timeout=5)
            dialog.child_window(title_re="はい.*",control_type="Button").invoke()
            for _ in range(40):
                if "青" in window.descendants(control_type="Edit")[0].get_value():break
                time.sleep(.15)
            assert "青" in window.descendants(control_type="Edit")[0].get_value()
            print("Confirmed insertion passed",flush=True)
        finally:
            editor.terminate();editor.wait()
            if old_text is not None:clipboard_text(old_text)
    finally:
        if proc.poll() is None:
            subprocess.run(["taskkill","/PID",str(proc.pid),"/T","/F"],capture_output=True)
            proc.wait()





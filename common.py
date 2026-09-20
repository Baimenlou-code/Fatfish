
import os
import time
from datetime import datetime

__all__ = ["ts", "ts_of", "dated_dir", "TS_FMT"]

TS_FMT = "%Y-%m-%d %H:%M:%S"

def ts():
    try:
        return datetime.now().strftime(TS_FMT)
    except Exception:
        return ""

def ts_of(t=None):
    if t is None:
        return ts()
    try:
        if isinstance(t, datetime):
            return t.strftime(TS_FMT)
        return time.strftime(TS_FMT, time.localtime(float(t)))
    except Exception:
        return ""

def dated_dir(root):
    try:
        root = root or "."
        now = datetime.now()
        d = os.path.join(root, f"{now:%Y}", f"{now:%m}", f"{now:%d}")
        os.makedirs(d, exist_ok=True)
        return d
    except Exception:
        try:
            now = datetime.now()
            return os.path.join(str(root), f"{now:%Y}", f"{now:%m}", f"{now:%d}")
        except Exception:
            return str(root or ".")

def _selftest():
    ok = True

    t = ts()
    good = (len(t) == 19 and t[4] == "-" and t[13] == ":" and t[16] == ":")
    print("ts()        = %-24r %s" % (t, "OK" if good else "FAIL"))
    ok = ok and good

    t2 = ts_of(1789744091.0)
    good = (len(t2) == 19)
    print("ts_of(ts)   = %-24r %s" % (t2, "OK" if good else "FAIL"))
    ok = ok and good

    good = (ts_of(None) == ts()[:19] or len(ts_of(None)) == 19)
    print("ts_of(None) = %-24r %s" % (ts_of(None), "OK" if good else "FAIL"))
    ok = ok and good

    import tempfile
    base = tempfile.mkdtemp(prefix="fatfish_common_")
    d = dated_dir(base)
    good = os.path.isdir(d) and d.startswith(base)
    print("dated_dir() = %-24r %s" % (os.path.relpath(d, base), "OK" if good else "FAIL"))
    ok = ok and good

    good = (dated_dir(base) == d)
    print("dated_dir() idempotent          %s" % ("OK" if good else "FAIL"))
    ok = ok and good

    try:
        dated_dir(None)
        dated_dir("")
        print("edge(root=None/'')              OK")
    except Exception as e:
        ok = False
        print("edge(root=None/'')              FAIL:", e)

    try:
        import shutil
        shutil.rmtree(base, ignore_errors=True)
    except Exception:
        pass

    print("\n%s" % ("all passed" if ok else "FAILURES present"))
    return 0 if ok else 1

if __name__ == "__main__":
    import sys
    sys.exit(_selftest())

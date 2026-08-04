#!/usr/bin/env python3
"""
抽取全项目里硬编码的中文界面文案，生成待翻译清单 + 可直接粘进 Lang.zh-Hans.xaml 的条目。

为什么需要这个脚本
------------------
"翻译成所有语言"这件事的真正工作量不在翻译，而在**抽字符串**：
项目里 1700+ 条中文是直接写死在 XAML 属性和 C# 字面量里的，
不先把它们变成 Lang.*.xaml 里的 key，翻译无从谈起。

用法
----
    python3 Tools/extract-hardcoded-strings.py            # 只统计，不改任何文件
    python3 Tools/extract-hardcoded-strings.py --report   # 输出 CSV 待翻译清单
    python3 Tools/extract-hardcoded-strings.py --emit-xaml # 输出可粘贴的 <system:String> 条目

这个脚本**只读不写源码**，不会自动改 XAML/C#。原因：
自动替换 C# 字符串很容易误伤（日志文本、异常消息、拼接片段、正则），
一旦替错，报错信息会变成 key 名，排查成本远高于手工。
正确流程是：用这个脚本产出清单 → 人工挑出真正的界面文案 → 逐块替换。
XAML 那 600 多条相对安全，可以按 --emit-xaml 的输出批量处理。
"""
import re, os, sys, glob, csv, collections

CJK = re.compile(r'[\u4e00-\u9fff]')
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

# XAML 中承载界面文案的属性
XAML_ATTRS = ('Text', 'Content', 'ToolTip', 'Header', 'Title', 'Watermark', 'Description')

# C# 里这些上下文的中文**不是**界面文案，跳过
CS_SKIP_CONTEXT = (
    'Debug.', 'Trace.', 'Console.', 'Log(', 'Logger', 'AppendLine("[',
    '// ', '/// ',
)

def scan_xaml():
    hits = collections.defaultdict(list)
    for f in glob.glob(os.path.join(ROOT, 'Views', '*.xaml')) + \
             glob.glob(os.path.join(ROOT, '*.xaml')):
        src = open(f, encoding='utf-8').read()
        src_nc = re.sub(r'<!--.*?-->', '', src, flags=re.S)   # 去注释
        for attr in XAML_ATTRS:
            for m in re.finditer(attr + r'="([^"]*)"', src_nc):
                v = m.group(1)
                if not CJK.search(v):
                    continue
                if v.startswith('{'):      # 已经是绑定/DynamicResource
                    continue
                line = src_nc[:m.start()].count('\n') + 1
                hits[v].append((os.path.relpath(f, ROOT), line, attr))
    return hits

def scan_cs():
    hits = collections.defaultdict(list)
    for f in glob.glob(os.path.join(ROOT, '**', '*.cs'), recursive=True):
        if os.sep + 'Tools' + os.sep in f:
            continue
        for i, line in enumerate(open(f, encoding='utf-8'), 1):
            stripped = line.strip()
            if stripped.startswith('//'):
                continue
            if any(sk in line for sk in CS_SKIP_CONTEXT):
                continue
            for m in re.finditer(r'"((?:[^"\\]|\\.)*)"', line):
                v = m.group(1)
                if not CJK.search(v) or len(v) < 2:
                    continue
                hits[v].append((os.path.relpath(f, ROOT), i, 'cs'))
    return hits

def suggest_key(text, seen):
    """给一条文案起一个稳定的 key。人工再改成更有语义的名字。"""
    base = 'Str_Auto_' + re.sub(r'[^A-Za-z0-9]', '', text.encode('unicode_escape')
                                .decode('ascii'))[:24]
    key = base or 'Str_Auto_X'
    n = 2
    while key in seen:
        key = f'{base}_{n}'; n += 1
    seen.add(key)
    return key

def main():
    xaml = scan_xaml()
    cs = scan_cs()

    print(f'XAML 界面属性里的中文 : {len(xaml):5d} 条唯一 / {sum(len(v) for v in xaml.values())} 处')
    print(f'C#  字符串字面量的中文: {len(cs):5d} 条唯一 / {sum(len(v) for v in cs.values())} 处')
    print(f'合计                  : {len(xaml) + len(cs)} 条唯一')

    lang_dir = os.path.join(ROOT, 'Resources', 'Lang')
    base = os.path.join(lang_dir, 'Lang.zh-Hans.xaml')
    if os.path.exists(base):
        done = len(re.findall(r'x:Key="', open(base, encoding='utf-8').read()))
        total = len(xaml) + len(cs) + done
        print(f'已抽出为 key          : {done} 条（覆盖率约 {done * 100 // max(total,1)}%）')

    if '--report' in sys.argv:
        out = os.path.join(ROOT, 'Tools', 'untranslated.csv')
        seen = set()
        with open(out, 'w', newline='', encoding='utf-8-sig') as fh:
            w = csv.writer(fh)
            w.writerow(['建议key', '中文原文', '出现次数', '来源', '首次出现位置'])
            for src_name, d in (('xaml', xaml), ('cs', cs)):
                for text, locs in sorted(d.items(), key=lambda kv: -len(kv[1])):
                    w.writerow([suggest_key(text, seen), text, len(locs), src_name,
                                f'{locs[0][0]}:{locs[0][1]}'])
        print(f'已写出待翻译清单: {out}')

    if '--emit-xaml' in sys.argv:
        out = os.path.join(ROOT, 'Tools', 'new-keys.zh-Hans.xaml.txt')
        seen = set()
        with open(out, 'w', encoding='utf-8') as fh:
            for text, locs in sorted(xaml.items(), key=lambda kv: -len(kv[1])):
                key = suggest_key(text, seen)
                esc = text.replace('&', '&amp;').replace('<', '&lt;').replace('>', '&gt;')
                fh.write(f'    <system:String x:Key="{key}">{esc}</system:String>\n')
        print(f'已写出可粘贴条目: {out}')

if __name__ == '__main__':
    main()

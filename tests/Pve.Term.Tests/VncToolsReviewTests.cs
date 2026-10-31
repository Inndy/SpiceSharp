using System.Diagnostics;

namespace Pve.Term.Tests;

public class VncToolsReviewTests
{
    [Fact]
    public async Task HelperBuildsVncArgvAndProbeReadsASplitUpdateWithoutRealConnections()
    {
        // Execute selected AST nodes only. Never import the credential-reading pve-vv module body.
        const string script = """
            import ast, os, pathlib, struct, sys, time
            from types import SimpleNamespace
            root = pathlib.Path(sys.argv[1])
            helper = root / 'tools' / 'pve-vv.py'
            tree = ast.parse(helper.read_text(encoding='utf-8'))
            original = next(n for n in tree.body if isinstance(n, ast.FunctionDef) and n.name == 'term_main')
            def extract(body, env):
                fn = ast.FunctionDef(name='selected', args=original.args, body=body,
                                     decorator_list=[], returns=None, type_params=[])
                module = ast.fix_missing_locations(ast.Module(body=[fn], type_ignores=[]))
                exec(compile(module, '<offline VNC helper>', 'exec'), env)
                return env['selected']
            stop = next(i for i, n in enumerate(original.body) if isinstance(n, ast.Assign)
                        and isinstance(n.targets[0], ast.Tuple)
                        and isinstance(n.targets[0].elts[0], ast.Name) and n.targets[0].elts[0].id == 'kind')
            env = {'os':os, '__file__':str(helper)}
            select = extract(original.body[1:stop] + ast.parse(
                'return opts, vnc, vnc_args, viewer_command, command, host, command_given').body, env)
            custom = [r'C:\viewer folder\viewer.exe', '{hostport}', '{title}', 'quote" & ; tail\\']
            opts, vnc, vnc_args, viewer_command, command, host, command_given = select(
                ['vnc:100', '--here', '--viewer', 'none', '--keepalive', '0', '--wait', '2', '--'] + custom)
            assert vnc and host is None and command is None and not command_given
            assert vnc_args == ['--viewer', 'none', '--keepalive', '0', '--wait', '2']
            assert viewer_command == custom
            captured = {}
            def popen(args, **kw):
                captured.update(args=args, **kw)
                return SimpleNamespace(wait=lambda:0)
            opts['exe'] = r'C:\bridge folder\PveTerm.exe'
            env.update(opts=opts, vnc=vnc, vnc_args=vnc_args, viewer_command=viewer_command,
                       command=command, host=host, command_given=command_given, root=str(root),
                       kind='vnc', spec='100', d={}, name='pveterm-synthetic', nonce='a'*32,
                       subprocess=SimpleNamespace(Popen=popen),
                       sys=SimpleNamespace(stdout=None, stderr=None),
                       server=SimpleNamespace(join=lambda **kw:None), served={'why':'served'},
                       print=lambda *a, **kw:None)
            start = next(i for i, n in enumerate(original.body) if isinstance(n, ast.Assign)
                         and isinstance(n.targets[0], ast.Name) and n.targets[0].id == 'exe')
            assert extract(ast.parse('command = None').body + original.body[start:], env)([]) == 0
            assert captured['args'] == [opts['exe'], 'vnc', '--pipe', 'pveterm-synthetic',
                                        '--nonce', 'a'*32] + vnc_args + ['--'] + custom
            assert captured['creationflags'] == 0 and 'shell' not in captured

            probe = ast.parse((root / 'tools' / 'vnc-probe.py').read_text(encoding='utf-8'))
            # Constants and function definitions only, excluding the __main__ entrypoint and imports.
            nodes = [n for n in probe.body if isinstance(n, (ast.Assign, ast.FunctionDef))]
            wire = bytearray(b'RFB 003.008\n\x01\x01' + bytes(4))
            wire += struct.pack('>HHBBBBHHHBBB3xI', 1, 1, 32, 24, 0, 1, 255, 255, 255, 16, 8, 0, 1) + b'x'
            wire += b'\0\0\0\1' + struct.pack('>HHHHi', 0, 0, 1, 1, 0) + bytes(4)
            sent, output = [], []
            class Socket:
                def setsockopt(self, *a): pass
                def recv(self, n):
                    data = bytes(wire[:min(n, 3)])
                    del wire[:len(data)]
                    return data
                def sendall(self, data): sent.append(data)
            def connect(address, **kw):
                assert address == ('127.0.0.1', 4321)
                return Socket()
            env = dict(struct=struct, time=time, sys=SimpleNamespace(argv=['probe', '4321']),
                       socket=SimpleNamespace(create_connection=connect, IPPROTO_TCP=6, TCP_NODELAY=1),
                       print=lambda *a, **kw:output.append(' '.join(map(str,a))))
            exec(compile(ast.Module(body=nodes, type_ignores=[]), '<offline VNC probe>', 'exec'), env)
            env['main']()
            assert not wire and output[-1] == 'probe: done'
            assert sent[:3] == [b'RFB 003.008\n', b'\1', b'\1']
            assert sent[-1] == struct.pack('>BBHHHH', 3, 0, 0, 0, 1, 1)
            assert len(sent) == 5 and sent[3][0] == 2  # No key, pointer or clipboard input.
            print('offline VNC tools controls passed')
            """;
        var psi = new ProcessStartInfo("python")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-I", "-B", "-c", script, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")) })
            psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0, await stderr);
            Assert.Contains("offline VNC tools controls passed", await stdout);
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}

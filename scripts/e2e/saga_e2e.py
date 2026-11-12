"""A saga de verdade, entre os três serviços no cluster, pelo API Gateway.

    python saga_e2e.py iniciar             até o link de pagamento
    python saga_e2e.py recusar             cliente recusa o orçamento
    python saga_e2e.py continuar <osId>    depois de pagar: executa e finaliza a OS
    python saga_e2e.py falhar <osId>       depois de pagar: execução falha e a saga compensa
    python saga_e2e.py ver <osId>          situação nos três serviços
"""
import json, random, subprocess, sys, time, urllib.error, urllib.request

try:
    sys.stdout.reconfigure(encoding="utf-8")
except AttributeError:
    pass


def aws(*args):
    return subprocess.run(["aws", *args, "--region", "us-east-1", "--output", "text"],
                          capture_output=True, text=True, check=True).stdout.strip()


GATEWAY = aws("ssm", "get-parameter", "--name", "/garageos/producao/apigw/url", "--query", "Parameter.Value")
APP = json.loads(aws("secretsmanager", "get-secret-value", "--secret-id", "garageos/app/secrets", "--query", "SecretString"))


# Um id por execucao do script: e ele que se procura nos logs dos tres servicos.
CORRELACAO = "e2e-" + time.strftime("%H%M%S") + "-" + str(random.randint(1000, 9999))


def chamar(metodo, caminho, corpo=None, token=None, aceitar=()):
    pedido = urllib.request.Request(GATEWAY + caminho, method=metodo, data=None if corpo is None else json.dumps(corpo).encode())
    pedido.add_header("Content-Type", "application/json")
    pedido.add_header("X-Correlation-Id", CORRELACAO)
    if token:
        pedido.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(pedido, timeout=30) as r:
            texto = r.read().decode()
            return json.loads(texto) if texto else None
    except urllib.error.HTTPError as e:
        if e.code in aceitar:
            return None
        sys.exit(f"{metodo} {caminho} -> HTTP {e.code}: {e.read().decode()[:400]}")


TOKEN = chamar("POST", "/os/api/auth/login", {"username": "admin", "password": APP["adminPassword"]})["token"]


def api(metodo, caminho, corpo=None, aceitar=()):
    return chamar(metodo, caminho, corpo, TOKEN, aceitar)


def cpf():
    n = [random.randint(0, 9) for _ in range(9)]
    for _ in range(2):
        d = sum(x * y for x, y in zip(n, range(len(n) + 1, 1, -1))) * 10 % 11
        n.append(0 if d == 10 else d)
    return "".join(map(str, n))


def esperar_saga(os_id, *etapas, limite=120):
    inicio = time.time()
    while time.time() - inicio < limite:
        saga = api("GET", f"/os/api/ordensdeservico/{os_id}/saga", aceitar=(404,))
        if saga and saga.get("etapa") in etapas:
            print(f"  saga: {saga['etapa']}  ({time.time() - inicio:.1f}s)")
            return saga
        time.sleep(1)
    sys.exit(f"Saga não chegou a {etapas} em {limite}s. Última: {saga}")


def orcamento_da_os(os_id, limite=60):
    inicio = time.time()
    while time.time() - inicio < limite:
        o = api("GET", f"/billing/api/orcamentos?ordemDeServicoId={os_id}", aceitar=(404,))
        if o:
            return o
        time.sleep(1)
    sys.exit("Billing não gerou o orçamento.")


def abrir_os():
    sufixo = random.randint(10000, 99999)
    cliente = api("POST", "/os/api/clientes", {
        "nome": f"Cliente Teste {sufixo}", "documento": cpf(), "email": f"teste{sufixo}@garageos.test", "telefone": f"119{sufixo}{sufixo % 1000:03d}",
        "logradouro": "Rua das Flores", "numero": "123", "complemento": "", "bairro": "Centro", "cidade": "São Paulo",
        "estado": "SP", "cep": "01001000"})
    veiculo = api("POST", "/os/api/veiculos", {
        "marcaVeiculo": "Volkswagen", "modeloVeiculo": "Gol", "placaVeiculo": f"TST{sufixo % 10}A{sufixo % 100:02d}",
        "anoVeiculo": 2020, "precoVeiculo": 30000})
    servico = api("POST", "/os/api/servicos", {"nomeServico": f"Revisão {sufixo}", "preco": 80.00})
    peca = api("POST", "/execucao/api/estoques", {
        "nome": f"Filtro {sufixo}", "quantidade": 10, "valor": 10.00, "fornecedor": "Fornecedor Teste",
        "dataEntrada": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
    os_ = api("POST", "/os/api/ordensdeservico/abertura-completa", {
        "clienteId": cliente["id"], "veiculoId": veiculo["id"], "servicosIds": [servico["id"]],
        "pecas": [{"estoqueId": peca["id"], "quantidade": 2}]})
    print("OS:", os_["id"], os_.get("numeroOS"), "status:", os_.get("status"))
    api("POST", f"/os/api/ordensdeservico/{os_['id']}/orcamento/solicitar")
    print("Orçamento pedido.")
    esperar_saga(os_["id"], "AguardandoAprovacao")
    return os_["id"]


def ver(os_id):
    os_ = api("GET", f"/os/api/ordensdeservico/{os_id}")
    saga = api("GET", f"/os/api/ordensdeservico/{os_id}/saga", aceitar=(404,)) or {}
    print("OS:      ", os_.get("numeroOS"), "|", os_.get("status"), "| saga:", saga.get("etapa"), "| motivo:", saga.get("motivo"))
    historico = api("GET", f"/os/api/ordensdeservico/{os_id}/historico") or []
    print("Histórico:", " -> ".join(str(h.get("statusNovo")) for h in historico))
    o = api("GET", f"/billing/api/orcamentos?ordemDeServicoId={os_id}", aceitar=(404,))
    if o:
        print("Orçamento:", o["id"], "|", o.get("status"), "| total:", o.get("total"))
        for p in api("GET", f"/billing/api/orcamentos/{o['id']}/pagamentos") or []:
            print("Pagamento:", p["id"], "|", p["status"], "| MP:", p.get("referenciaExterna"))
    r = api("GET", f"/execucao/api/reservas/{os_id}", aceitar=(404,))
    if r:
        print("Reserva:  ", r.get("status"))
    for e in api("GET", "/execucao/api/execucoes") or []:
        if e.get("ordemDeServicoId") == os_id:
            print("Execução: ", e["id"], "|", e.get("status"), "| motivo:", e.get("motivoFalha"))


def execucao_da_os(os_id, limite=60):
    inicio = time.time()
    while time.time() - inicio < limite:
        for e in api("GET", "/execucao/api/execucoes") or []:
            if e.get("ordemDeServicoId") == os_id:
                return e
        time.sleep(1)
    sys.exit("O Execução não abriu a ordem de execução.")


acao = sys.argv[1] if len(sys.argv) > 1 else ""

print("correlationId desta execucao:", CORRELACAO)

if acao == "iniciar":
    os_id = abrir_os()
    o = orcamento_da_os(os_id)
    print("Orçamento:", o["id"], "total:", o.get("total"))
    api("PATCH", f"/billing/api/orcamentos/{o['id']}/resposta", {"aprovado": True})
    print("Cliente aprovou.")
    esperar_saga(os_id, "AguardandoPagamento")
    inicio = time.time()
    link = None
    while not link and time.time() - inicio < 60:
        pagamentos = api("GET", f"/billing/api/orcamentos/{o['id']}/pagamentos") or []
        link = next((p.get("linkPagamento") for p in pagamentos if p.get("linkPagamento")), None)
        time.sleep(1)
    print("\nOS para os próximos comandos:", os_id)
    print("Link de pagamento (vale 10 minutos):")
    print(link)
elif acao == "recusar":
    os_id = abrir_os()
    o = orcamento_da_os(os_id)
    api("PATCH", f"/billing/api/orcamentos/{o['id']}/resposta", {"aprovado": False})
    print("Cliente recusou.")
    esperar_saga(os_id, "Cancelada")
    ver(os_id)
elif acao == "continuar":
    os_id = sys.argv[2]
    esperar_saga(os_id, "EmExecucao", limite=30)
    e = execucao_da_os(os_id)
    api("PATCH", f"/execucao/api/execucoes/{e['id']}/status", {"status": "EmDiagnostico"})
    api("PATCH", f"/execucao/api/execucoes/{e['id']}/status", {"status": "EmReparo"})
    for item in e.get("itens", []):
        api("PATCH", f"/execucao/api/execucoes/{e['id']}/itens/{item['id']}/status", {"status": "Iniciado"})
        api("PATCH", f"/execucao/api/execucoes/{e['id']}/itens/{item['id']}/status", {"status": "Finalizado"})
    api("PATCH", f"/execucao/api/execucoes/{e['id']}/status", {"status": "Concluida"})
    esperar_saga(os_id, "Finalizada")
    ver(os_id)
elif acao == "falhar":
    os_id = sys.argv[2]
    esperar_saga(os_id, "EmExecucao", limite=30)
    e = execucao_da_os(os_id)
    api("PATCH", f"/execucao/api/execucoes/{e['id']}/status", {"status": "Falhou", "motivo": "Motor travado"})
    esperar_saga(os_id, "Cancelada")
    time.sleep(8)
    ver(os_id)
elif acao == "abrir":
    print("OS aguardando o cliente:", abrir_os())
elif acao == "ver":
    ver(sys.argv[2])
else:
    sys.exit(__doc__)

using GarageOS.Domain.Entities;
using GarageOS.Domain.Enums;
using GarageOS.Domain.Exceptions;
using GarageOS.Domain.Repositories;
using GarageOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace GarageOS.Infrastructure.Repositories;

public class OrdemDeServicoRepository : IOrdemDeServicoRepository
{
    private readonly GarageOSDbContext _context;

    public OrdemDeServicoRepository(GarageOSDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<OrdemDeServico>> ListarTodosAsync()
    {
        return await _context.OrdensDeServico
            .AsNoTracking()
            .Where(x => x.Status != StatusOrdemDeServico.Entregue
                 && x.Status != StatusOrdemDeServico.Cancelada)
            .Include(x => x.Servicos)
            .Include(x => x.Pecas)
            .Include(x => x.Historico)
            .OrderBy(x => x.Status == StatusOrdemDeServico.EmExecucao ? 0
                    : x.Status == StatusOrdemDeServico.AguardandoPagamento ? 1
                    : x.Status == StatusOrdemDeServico.AguardandoAprovacao ? 2
                    : x.Status == StatusOrdemDeServico.EmDiagnostico ? 3
                    : x.Status == StatusOrdemDeServico.Recebida ? 4
                    : 5)
            .ThenBy(x => x.CriadoEm)
            .AsSplitQuery()
            .ToListAsync();
    }

    /// <remarks>Com tracking: quem chama altera a OS e grava com AtualizarAsync.</remarks>
    public async Task<OrdemDeServico?> ObterPorIdAsync(Guid id)
    {
        return await _context.OrdensDeServico
            .Include(x => x.Servicos)
            .Include(x => x.Pecas)
            .Include(x => x.Historico)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    public async Task<OrdemDeServico?> ObterPorNumeroOSAsync(string numeroOS)
    {
        return await _context.OrdensDeServico
            .AsNoTracking()
            .Include(x => x.Servicos)
            .Include(x => x.Historico)
            .AsSplitQuery()
            .FirstOrDefaultAsync(x => x.NumeroOS == numeroOS);
    }

    public async Task AdicionarAsync(OrdemDeServico ordemDeServico)
    {
        _context.OrdensDeServico.Add(ordemDeServico);
        await _context.SaveChangesAsync();
    }

    /// <remarks>
    /// A OS veio do ObterPorIdAsync, com tracking. Itens e histórico novos são
    /// detectados pelas coleções e entram como inseridos. Update() aqui marcaria
    /// esses itens como alterados, e o UPDATE de uma linha inexistente falharia.
    /// </remarks>
    public async Task AtualizarAsync(OrdemDeServico ordemDeServico)
    {
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new OrdemDeServicoStatusInvalidoException(
                $"A OS {ordemDeServico.NumeroOS} foi alterada por outra requisição ao mesmo tempo. Tente novamente.", ex);
        }
    }

    public async Task<int> ObterProximoSequencialDoAnoAsync(int ano)
    {
        // Uma sequência por ano, criada sob demanda. O nome vem de um inteiro,
        // não de entrada do usuário, então a interpolação é segura. Parâmetro não
        // serve aqui: SQL não aceita parâmetro no lugar do nome de um objeto.
        var sequencia = $"\"SeqNumeroOS{ano}\"";

        await _context.Database.ExecuteSqlRawAsync($"CREATE SEQUENCE IF NOT EXISTS {sequencia}"); // NOSONAR

        var proximo = await _context.Database
            .SqlQueryRaw<long>($"SELECT nextval('{sequencia}') AS \"Value\"") // NOSONAR
            .SingleAsync();

        return (int)proximo;
    }
}

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OASIS.WebAPI.Migrations
{
    /// <summary>
    /// Snapshot-delta only — intentionally EMPTY Up/Down (no DDL).
    ///
    /// <para>This migration records two model changes that produce NO schema
    /// DDL:</para>
    /// <list type="bullet">
    /// <item>Removal of the vestigial <c>xmin</c>/<c>Version</c>
    /// optimistic-concurrency token on <c>BridgeTransactions</c> and
    /// <c>SagaSteps</c>. <c>xmin</c> is a PostgreSQL <b>system column</b> that
    /// exists on every table automatically; the prior model only <em>mapped</em>
    /// it (<c>HasColumnName("xmin")</c>) — it was never created by DDL, so
    /// there is nothing to drop. The EF scaffolder emitted a
    /// <c>DropColumn("xmin")</c>/<c>AddColumn</c> pair purely to reconcile its
    /// model snapshot; running that against real Postgres would error on the
    /// system column. Emptied per the greenfield no-compat-SQL rule — the
    /// authoritative delta is the model snapshot.</item>
    /// <item>The new <c>BridgeStatus.Reversing</c> enum value. The enum is
    /// persisted as <c>int</c> and the value is appended, so no schema change.</item>
    /// </list>
    /// Greenfield: the DB is built fresh from empty via <c>Migrate()</c>; this
    /// inert migration keeps the snapshot history consistent without compat shims.
    /// </summary>
    /// <inheritdoc />
    public partial class RemoveXminTokensAndAddReversingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see class summary (no-DDL snapshot delta).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty — see class summary (no-DDL snapshot delta).
        }
    }
}

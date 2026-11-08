using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OrderFlow.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ImproveQueueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Status",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_InventoryReservations_ProductId",
                table: "InventoryReservations");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status_CreatedAtUtc",
                table: "Orders",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_ProductId_CreatedAtUtc",
                table: "InventoryReservations",
                columns: new[] { "ProductId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_Status_CreatedAtUtc",
                table: "Orders");

            migrationBuilder.DropIndex(
                name: "IX_InventoryReservations_ProductId_CreatedAtUtc",
                table: "InventoryReservations");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_Status",
                table: "Orders",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryReservations_ProductId",
                table: "InventoryReservations",
                column: "ProductId");
        }
    }
}

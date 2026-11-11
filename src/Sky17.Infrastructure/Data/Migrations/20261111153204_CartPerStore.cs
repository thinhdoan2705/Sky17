using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sky17.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class CartPerStore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Carts become one-per-store. A cart without a store is empty by construction; remove it
            // (and any stray items) so StoreId can become NOT NULL. Carts that have a store are kept as-is.
            migrationBuilder.Sql("DELETE FROM CartItems WHERE CartId IN (SELECT Id FROM Carts WHERE StoreId IS NULL);");
            migrationBuilder.Sql("DELETE FROM Carts WHERE StoreId IS NULL;");

            migrationBuilder.DropForeignKey(
                name: "FK_Carts_Stores_StoreId",
                table: "Carts");

            migrationBuilder.DropIndex(
                name: "IX_Carts_UserId",
                table: "Carts");

            migrationBuilder.AlterColumn<int>(
                name: "StoreId",
                table: "Carts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Carts_UserId_StoreId",
                table: "Carts",
                columns: new[] { "UserId", "StoreId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Carts_Stores_StoreId",
                table: "Carts",
                column: "StoreId",
                principalTable: "Stores",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Carts_Stores_StoreId",
                table: "Carts");

            migrationBuilder.DropIndex(
                name: "IX_Carts_UserId_StoreId",
                table: "Carts");

            migrationBuilder.AlterColumn<int>(
                name: "StoreId",
                table: "Carts",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_Carts_UserId",
                table: "Carts",
                column: "UserId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Carts_Stores_StoreId",
                table: "Carts",
                column: "StoreId",
                principalTable: "Stores",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}

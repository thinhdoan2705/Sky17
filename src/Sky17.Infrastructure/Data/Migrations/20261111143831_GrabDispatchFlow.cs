using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sky17.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class GrabDispatchFlow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Map statuses of the previous flow onto the GrabFood flow:
            // Preparing(2)/ReadyForPickup(3) → Confirmed(1) (store accepted, searching driver); PickedUp(5) → Delivering(6).
            migrationBuilder.Sql("UPDATE \"Orders\" SET \"Status\" = 1 WHERE \"Status\" IN (2, 3);");
            migrationBuilder.Sql("UPDATE \"Orders\" SET \"Status\" = 6 WHERE \"Status\" = 5;");
            migrationBuilder.Sql("UPDATE \"OrderStatusHistories\" SET \"NewStatus\" = 1 WHERE \"NewStatus\" IN (2, 3);");
            migrationBuilder.Sql("UPDATE \"OrderStatusHistories\" SET \"NewStatus\" = 6 WHERE \"NewStatus\" = 5;");
            migrationBuilder.Sql("UPDATE \"OrderStatusHistories\" SET \"PreviousStatus\" = 1 WHERE \"PreviousStatus\" IN (2, 3);");
            migrationBuilder.Sql("UPDATE \"OrderStatusHistories\" SET \"PreviousStatus\" = 6 WHERE \"PreviousStatus\" = 5;");

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchAlertedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DispatchStartedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FoodReadyAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreparationState",
                table: "Orders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "LastLatitude",
                table: "DriverProfiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLocationAtUtc",
                table: "DriverProfiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "LastLongitude",
                table: "DriverProfiles",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DispatchOffers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    DriverId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DriverDistanceMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    OfferedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RespondedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchOffers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchOffers_AspNetUsers_DriverId",
                        column: x => x.DriverId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DispatchOffers_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchOffers_DriverId_Pending",
                table: "DispatchOffers",
                column: "DriverId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchOffers_OrderId_DriverId",
                table: "DispatchOffers",
                columns: new[] { "OrderId", "DriverId" });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchOffers_OrderId_Pending",
                table: "DispatchOffers",
                column: "OrderId",
                unique: true,
                filter: "\"Status\" = 0");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchOffers_Status_ExpiresAtUtc",
                table: "DispatchOffers",
                columns: new[] { "Status", "ExpiresAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchOffers");

            migrationBuilder.DropColumn(
                name: "ConfirmedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DispatchAlertedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "DispatchStartedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "FoodReadyAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PreparationState",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "LastLatitude",
                table: "DriverProfiles");

            migrationBuilder.DropColumn(
                name: "LastLocationAtUtc",
                table: "DriverProfiles");

            migrationBuilder.DropColumn(
                name: "LastLongitude",
                table: "DriverProfiles");
        }
    }
}

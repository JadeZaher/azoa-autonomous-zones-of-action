// @vitest-environment jsdom
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defaultMoneyMessages } from "../../src/money/index.js";
import type { SaleView } from "../../src/money/index.js";
import { AzoaMoneyProvider, SellUnitsForm } from "../../src/react/index.js";
import type { SellUnitsFormProps } from "../../src/react/index.js";
import { createTenantBff } from "./tenant-bff.js";
import type { TenantBff } from "./tenant-bff.js";

const copy = defaultMoneyMessages;

function visitSalePage(bff: TenantBff, props: SellUnitsFormProps = {}) {
  return render(
    <AzoaMoneyProvider transport={bff.transport} subject="ada">
      <SellUnitsForm {...props} />
    </AzoaMoneyProvider>,
  );
}

async function quote(quantity: string) {
  const user = userEvent.setup();
  await screen.findByText(/Available to sell/);
  await user.type(screen.getByLabelText(copy.sale.quantityLabel), quantity);
  await user.click(screen.getByRole("button", { name: copy.sale.getQuote }));
  await screen.findByText(/^Sell .* for /);
}

beforeEach(() => {
  localStorage.clear();
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("<SellUnitsForm>", () => {
  it("a lost sale answer resumes the same quote under the same key, so the host sells once", async () => {
    const user = userEvent.setup();
    const bff = createTenantBff({ ada: {} });
    const settled: SaleView[] = [];
    const first = visitSalePage(bff, { onSettled: view => settled.push(view) });
    await quote("1.5");
    expect(screen.getByText("Sell 1.5 AZU for 1.50 USD.")).not.toBeNull();
    bff.loseNextSale();
    await user.click(screen.getByRole("button", { name: copy.sale.submit }));
    await screen.findByText("A sale for 1.50 USD has not been confirmed yet.");
    first.unmount();

    visitSalePage(bff, { onSettled: view => settled.push(view) });
    await user.click(await screen.findByRole("button", { name: copy.pending.resume.sale }));

    await screen.findByText("Sold. The money is on its way.");
    expect(bff.sales).toHaveLength(1);
    expect(settled.map(view => view.proceedsMinorUnits)).toEqual([150]);
  });

  it("a host onSettled that throws leaves the form usable", async () => {
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const user = userEvent.setup();
    const bff = createTenantBff({ ada: {} });
    visitSalePage(bff, { onSettled: () => { throw new Error("analytics down"); } });
    await quote("1");
    await user.click(screen.getByRole("button", { name: copy.sale.submit }));

    await screen.findByText("Sold. The money is on its way.");
    await waitFor(() => expect((screen.getByRole("button", { name: copy.sale.getQuote }) as HTMLButtonElement).disabled).toBe(false));
  });

  it("renders nothing when the host transport cannot buy units", () => {
    const bff = createTenantBff({ ada: {} });
    const { sale: _sale, ...withoutSale } = bff.transport;
    const { container } = render(
      <AzoaMoneyProvider transport={withoutSale} subject="ada">
        <SellUnitsForm />
      </AzoaMoneyProvider>,
    );
    expect(container.innerHTML).toBe("");
  });
});

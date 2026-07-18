import { describe, it, expect, vi, beforeEach } from "vitest";
import { AzoaApiClient } from "../../src/api/client.js";
import { HolonQueryBuilder } from "../../src/client/holon-query.js";
import { API_PATHS } from "../../src/api/api-version.js";
import { ApiConfigBuilder } from "../builders/index.js";

const mockFetch = vi.fn();
vi.stubGlobal("fetch", mockFetch);

function azoaResponse<T>(result: T) {
  return Promise.resolve({
    ok: true,
    status: 200,
    json: () => Promise.resolve({ isError: false, result }),
  });
}

describe("HolonQueryBuilder.getComposite (Finding 8)", () => {
  let api: AzoaApiClient;
  let holons: HolonQueryBuilder;

  beforeEach(() => {
    mockFetch.mockReset();
    api = new AzoaApiClient(new ApiConfigBuilder().withToken("t").build());
    holons = new HolonQueryBuilder(api);
  });

  it("builds the compose URL via API_PATHS.HOLON_COMPOSE(id)", async () => {
    const id = "abc-123";
    mockFetch.mockReturnValueOnce(azoaResponse({ composed: true }));

    await holons.getComposite(id);

    const expected = `http://localhost:5000${API_PATHS.HOLON_COMPOSE(id)}`;
    expect(mockFetch).toHaveBeenCalledWith(
      expected,
      expect.objectContaining({ method: "GET" })
    );
    expect(expected).toBe(`http://localhost:5000/api/holon/${id}/compose`);
  });
});

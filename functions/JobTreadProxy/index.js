const GRANT_KEY = process.env.JOBTREAD_GRANT_KEY;
const ORG_ID    = "22PWNY9u7qZd";
const PAVE_URL  = "https://api.jobtread.com/pave";

const corsHeaders = {
  "Access-Control-Allow-Origin": "*",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
  "Access-Control-Allow-Headers": "Content-Type",
  "Content-Type": "application/json",
};

async function pave(query) {
  const body = { query: { "$": { "grantKey": GRANT_KEY }, ...query } };
  const res = await fetch(PAVE_URL, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const text = await res.text();
  try { return JSON.parse(text); }
  catch(e) { throw new Error("JobTread returned: " + text.slice(0, 200)); }
}

module.exports = async function (context, req) {

  if (req.method === "OPTIONS") {
    context.res = { status: 204, headers: corsHeaders };
    return;
  }

  try {
    const { action, jobId, rooms, multiplier } = req.body || {};

    if (action === "getJobs") {
      const data = await pave({
        organization: {
          $: { id: ORG_ID },
          jobs: {
            $: { size: 100, sortBy: [{ field: "createdAt", order: "desc" }] },
            nodes: { id: {}, name: {}, location: { address: {} } }
          }
        }
      });
      const jobs = (data?.query?.organization?.jobs?.nodes || []).map(j => ({
        id: j.id, name: j.name, address: j.location?.address || "",
      }));
      context.res = { status: 200, headers: corsHeaders, body: JSON.stringify({ jobs }) };
      return;
    }

    if (action === "buildEstimate") {
      const CATALOG = {
        walls: {
          "1coat":  { id: "22PWT9nDL9HP", name: "Drywall Walls - Existing - 1 Coat",         unitCost: 0.75,   unitPrice: 1.00,   costCode: "22PWmcdKrCnn", unitId: "22PWNYKLitQU" },
          "2coats": { id: "22PWiSS293E2", name: "Drywall Walls - Existing - 2 Coats",         unitCost: 1.13,   unitPrice: 1.50,   costCode: "22PWmcdKrCnn", unitId: "22PWNYKLitQU" },
          "prime2": { id: "22PWiSgjqkaq", name: "Drywall Walls - Existing - Prime + 2 Coats", unitCost: 1.32,   unitPrice: 1.76,   costCode: "22PWmcdKrCnn", unitId: "22PWNYKLitQU" },
        },
        ceilings: {
          "1coat":  { id: "22PWickkTi46", name: "Drywall Ceilings - Existing - 1 Coat",         unitCost: 0.83, unitPrice: 1.11, costCode: "22PWmcdMcqbZ", unitId: "22PWNYKLitQU" },
          "2coats": { id: "22PWictZ3V84", name: "Drywall Ceilings - Existing - 2 Coats",         unitCost: 1.24, unitPrice: 1.65, costCode: "22PWmcdMcqbZ", unitId: "22PWNYKLitQU" },
          "prime2": { id: "22PWicwN2pxL", name: "Drywall Ceilings - Existing - Prime + 2 Coats", unitCost: 1.65, unitPrice: 2.20, costCode: "22PWmcdMcqbZ", unitId: "22PWNYKLitQU" },
        },
        baseboard: {
          "1coat":  { id: "22PWiYQt5Pq8", name: "Wood Baseboard - Existing - 1-Coat",  unitCost: 1.50, unitPrice: 2.00, costCode: "22PWmcdjtJ9C", unitId: "22PWNYKLfXKr" },
          "2coats": { id: "22PWih8XPvPm", name: "Wood Baseboard - Existing - 2-Coats", unitCost: 2.25, unitPrice: 3.00, costCode: "22PWmcdjtJ9C", unitId: "22PWNYKLfXKr" },
        },
        doors: {
          "1coat":  { id: "22PXFmQz3HEd", name: "Wood Door Paint - Existing - 1-Coat",    unitCost: 93.75,  unitPrice: 125.00, costCode: "22PWmcdpBtSV", unitId: "22PWNYKLcv3n" },
          "2coats": { id: "22PXFmRbHAgn", name: "Wood Door Paint - Existing - 2-Coats",   unitCost: 131.25, unitPrice: 175.00, costCode: "22PWmcdpBtSV", unitId: "22PWNYKLcv3n" },
          "prime2": { id: "22PXFmSG2zeV", name: "Wood Door Paint - New - Prime + 2-Coats", unitCost: 206.25, unitPrice: 275.00, costCode: "22PWmcdpBtSV", unitId: "22PWNYKLcv3n" },
        },
      };

      const results = [];
      for (const room of (rooms || [])) {
        if (!room.length || !room.width) continue;
        const L = parseFloat(room.length), W = parseFloat(room.width), H = parseFloat(room.height || 9);

        const parentData = await pave({
          createParent: {
            $: { jobId, name: room.name, showChildCosts: true, showChildren: true, showDescription: true },
            _: "createCostGroup", createdCostGroup: { id: {}, name: {} },
          },
        });
        const parentId = parentData.query?.createParent?.createdCostGroup?.id || parentData.createParent?.createdCostGroup?.id;
        if (!parentId) { results.push({ room: room.name, error: "Failed parent" }); continue; }

        const scopes = [
          room.ceilings  !== "none" && CATALOG.ceilings[room.ceilings]  && { name: "Drywall Ceilings", formula: `${L} * ${W}`,              unitId: "22PWNYKLitQU", item: CATALOG.ceilings[room.ceilings] },
          room.walls     !== "none" && CATALOG.walls[room.walls]         && { name: "Drywall Walls",    formula: `2 * (${L} + ${W}) * ${H}`, unitId: "22PWNYKLitQU", item: CATALOG.walls[room.walls] },
          room.baseboard !== "none" && CATALOG.baseboard[room.baseboard] && { name: "Wood Baseboard",   formula: `2 * (${L} + ${W})`,        unitId: "22PWNYKLfXKr", item: CATALOG.baseboard[room.baseboard] },
          room.doors     !== "none" && parseInt(room.doorCount) > 0 && CATALOG.doors[room.doors] && { name: "Doors + Frames", formula: null, qty: parseInt(room.doorCount), unitId: "22PWNYKLcv3n", item: CATALOG.doors[room.doors] },
        ].filter(Boolean);

        for (const scope of scopes) {
          const sg = await pave({
            createSG: {
              $: { jobId, name: scope.name, parentCostGroupId: parentId, ...(scope.formula ? { quantityFormula: scope.formula } : {}), showChildCosts: true, showChildren: true, showDescription: true, unitId: scope.unitId },
              _: "createCostGroup", createdCostGroup: { id: {}, name: {} },
            },
          });
          const sgId = sg.query?.createSG?.createdCostGroup?.id || sg.createSG?.createdCostGroup?.id;
          if (!sgId) continue;
          await pave({
            createCI: {
              $: { jobId, costGroupId: sgId, organizationCostItemId: scope.item.id, name: scope.item.name, costCodeId: scope.item.costCode, costTypeId: "22PWNYKLxD4B", unitId: scope.unitId, ...(scope.formula ? { quantityFormula: "{Parent Quantity}" } : { quantity: scope.qty }), unitCost: Math.round(scope.item.unitCost * multiplier * 100) / 100, unitPrice: Math.round(scope.item.unitPrice * multiplier * 100) / 100, showDescription: true, showQuantity: true },
              _: "createCostItem", createdCostItem: { id: {}, name: {} },
            },
          });
        }
        results.push({ room: room.name, parentId, scopes: scopes.length });
      }
      context.res = { status: 200, headers: corsHeaders, body: JSON.stringify({ success: true, results }) };
      return;
    }

    context.res = { status: 400, headers: corsHeaders, body: JSON.stringify({ error: "Unknown action" }) };

  } catch (err) {
    context.log.error("Proxy error:", err.message);
    context.res = { status: 500, headers: corsHeaders, body: JSON.stringify({ error: err.message }) };
  }
};
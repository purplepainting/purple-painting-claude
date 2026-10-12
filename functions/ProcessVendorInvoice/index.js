const https = require('https');

const JT_ORG_ID = process.env.JT_ORG_ID || '22PWNY9u7qZd';
const JT_GRANT_KEY = process.env.JOBTREAD_GRANT_PROXY;
const ANTHROPIC_KEY = process.env.ANTHROPIC_API_KEY;
const GRAPH_TENANT = process.env.AZURE_TENANT_ID;
const GRAPH_CLIENT = process.env.AZURE_CLIENT_ID;
const GRAPH_SECRET = process.env.AZURE_CLIENT_SECRET;
const MAILBOX = 'payables@purplepainting.net';
const PROCESSED_LABEL = 'PP_PROCESSED';
const LOOKBACK_DAYS = 1;
const KAREEM_ID = '22PWNY9u7qZe';

function httpsPost(hostname, path, headers, body) {
  return new Promise((resolve, reject) => {
    const data = JSON.stringify(body);
    const req = https.request({ hostname, path, method: 'POST', headers: { ...headers, 'Content-Length': Buffer.byteLength(data) } }, res => {
      let raw = '';
      res.on('data', c => raw += c);
      res.on('end', () => { try { resolve(JSON.parse(raw)); } catch { resolve(raw); } });
    });
    req.on('error', reject);
    req.write(data);
    req.end();
  });
}

function httpsGet(hostname, path, headers) {
  return new Promise((resolve, reject) => {
    const req = https.request({ hostname, path, method: 'GET', headers }, res => {
      let raw = '';
      res.on('data', c => raw += c);
      res.on('end', () => { try { resolve(JSON.parse(raw)); } catch { resolve(raw); } });
    });
    req.on('error', reject);
    req.end();
  });
}

async function getGraphToken() {
  const body = `grant_type=client_credentials&client_id=${GRAPH_CLIENT}&client_secret=${encodeURIComponent(GRAPH_SECRET)}&scope=https%3A%2F%2Fgraph.microsoft.com%2F.default`;
  return new Promise((resolve, reject) => {
    const req = https.request({
      hostname: 'login.microsoftonline.com',
      path: `/${GRAPH_TENANT}/oauth2/v2.0/token`,
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded', 'Content-Length': Buffer.byteLength(body) }
    }, res => {
      let raw = '';
      res.on('data', c => raw += c);
      res.on('end', () => { try { resolve(JSON.parse(raw).access_token); } catch { reject(raw); } });
    });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

async function getUnprocessedEmails(token) {
  const since = new Date(Date.now() - LOOKBACK_DAYS * 24 * 60 * 60 * 1000).toISOString();
  const filter = encodeURIComponent(`receivedDateTime ge ${since} and not categories/any(c:c eq '${PROCESSED_LABEL}')`);
  const path = `/v1.0/users/${MAILBOX}/messages?$filter=${filter}&$select=id,subject,receivedDateTime,from,hasAttachments&$top=20`;
  return httpsGet('graph.microsoft.com', path, { Authorization: `Bearer ${token}` });
}

async function getAttachments(token, messageId) {
  const path = `/v1.0/users/${MAILBOX}/messages/${messageId}/attachments`;
  return httpsGet('graph.microsoft.com', path, { Authorization: `Bearer ${token}` });
}

async function markProcessed(token, messageId) {
  return new Promise((resolve, reject) => {
    const body = JSON.stringify({ categories: [PROCESSED_LABEL] });
    const req = https.request({
      hostname: 'graph.microsoft.com',
      path: `/v1.0/users/${MAILBOX}/messages/${messageId}`,
      method: 'PATCH',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }
    }, res => {
      let raw = '';
      res.on('data', c => raw += c);
      res.on('end', () => resolve(raw));
    });
    req.on('error', reject);
    req.write(body);
    req.end();
  });
}

async function extractInvoiceData(pdfBase64, filename, context) {
  const prompt = `You are parsing a vendor invoice PDF for a painting contractor.
Extract the following fields and return ONLY valid JSON, no explanation:
{
  "vendor_name": "exact vendor name from invoice",
  "invoice_number": "invoice or bill number",
  "invoice_date": "YYYY-MM-DD format",
  "due_date": "YYYY-MM-DD format or null",
  "total_amount": 0.00,
  "job_name": "job name, PO number, or project reference from invoice or null",
  "line_items": [
    { "description": "item description", "amount": 0.00 }
  ]
}
If a field is not found, use null. total_amount must be a number.`;

  const result = await httpsPost('api.anthropic.com', '/v1/messages', {
    'Content-Type': 'application/json',
    'x-api-key': ANTHROPIC_KEY,
    'anthropic-version': '2023-06-01'
  }, {
    model: 'claude-sonnet-4-6',
    max_tokens: 1000,
    messages: [{
      role: 'user',
      content: [
        { type: 'document', source: { type: 'base64', media_type: 'application/pdf', data: pdfBase64 } },
        { type: 'text', text: prompt }
      ]
    }]
  });

  context.log(`Claude result: ${JSON.stringify(result).substring(0, 500)}`);

  const text = result.content?.[0]?.text || '{}';
  try {
    return JSON.parse(text.replace(/```json|```/g, '').trim());
  } catch {
    return null;
  }
}

async function jtQuery(query) {
  return httpsPost('api.jobtread.com', '/pave', { 'Content-Type': 'application/json' }, {
    query: { $: { grantKey: JT_GRANT_KEY }, ...query }
  });
}

async function getJTVendors(context) {
  const body = {
    query: {
      $: { grantKey: JT_GRANT_KEY },
      organization: {
        $: { id: JT_ORG_ID },
        accounts: {
          $: { where: ['type', 'vendor'], size: 100 },
          nodes: { id: {}, name: {} }
        }
      }
    }
  };
  const res = await httpsPost('api.jobtread.com', '/pave', { 'Content-Type': 'application/json' }, body);
  context.log(`Vendors raw: ${JSON.stringify(res).substring(0, 200)}`);
  return res?.query?.organization?.accounts?.nodes || [];
}

async function getActiveJobs(context) {
  const body = {
    query: {
      $: { grantKey: JT_GRANT_KEY },
      organization: {
        $: { id: JT_ORG_ID },
        jobs: {
          $: { where: ['closedOn', null], size: 100 },
          nodes: { id: {}, name: {}, number: {}, location: { address: {} } }
        }
      }
    }
  };
  const res = await httpsPost('api.jobtread.com', '/pave', { 'Content-Type': 'application/json' }, body);
  context.log(`Jobs raw: ${JSON.stringify(res).substring(0, 200)}`);
  return (res?.query?.organization?.jobs?.nodes || []).map(j => ({
    ...j,
    address: j.location?.address || ''
  }));
}

function fuzzyMatch(input, candidates, key) {
  if (!input) return null;
  const norm = s => s?.toLowerCase().replace(/[^a-z0-9\s]/g, '').trim() || '';
  const words = norm(input).split(/\s+/).filter(w => w.length > 2);
  if (!words.length) return null;

  let best = null, bestScore = 0;
  for (const c of candidates) {
    const val = norm(c[key] || '');
    if (!val) continue;
    const matches = words.filter(w => val.includes(w)).length;
    const score = matches / words.length;
    if (score > bestScore) { bestScore = score; best = c; }
  }
  return bestScore >= 0.4 ? best : null;
}

async function createJTBill(vendorId, jobId, invoiceData, context) {
  const args = {
    type: 'vendorBill',
    name: `Bill - ${invoiceData.vendor_name || 'Unknown'} - ${invoiceData.invoice_date || new Date().toISOString().split('T')[0]}`,
    issueDate: invoiceData.invoice_date || new Date().toISOString().split('T')[0],
    fromName: invoiceData.vendor_name || 'Unknown Vendor',
    toName: 'Purple Painting Co.',
    taxRate: 0,
    dueDays: 30,
    lineItems: []
  };
  if (vendorId) args.accountId = vendorId;
  if (jobId) args.jobId = jobId;
  if (invoiceData.invoice_number) args.referenceNumber = invoiceData.invoice_number;

  const body = {
    query: {
      $: { grantKey: JT_GRANT_KEY },
      createDocument: {
        $: args,
        createdDocument: { id: {}, name: {} }
      }
    }
  };

  const res = await httpsPost('api.jobtread.com', '/pave', { 'Content-Type': 'application/json' }, body);
  context.log(`createDocument response: ${JSON.stringify(res)}`);
  return res?.query?.createDocument?.createdDocument;
}

async function createJTTodo(billId, invoiceData, matchedJob, vendorMatched, context) {
  const jobNote = matchedJob
    ? `Suggested job: ${matchedJob.name}`
    : `Job not found for: "${invoiceData.job_name || 'none on invoice'}" - assign manually`;

  const vendorNote = vendorMatched
    ? `Vendor: ${vendorMatched.name}`
    : `Vendor not matched: "${invoiceData.vendor_name}" - verify`;

  const note = `New bill needs review\n${vendorNote}\n${jobNote}\nAmount: $${invoiceData.total_amount}\nInvoice #: ${invoiceData.invoice_number || 'N/A'}\nDate: ${invoiceData.invoice_date || 'N/A'}`;

  const taskArgs = {
    organizationId: JT_ORG_ID,
    assigneeId: KAREEM_ID,
    isToDo: true,
    name: `Review bill: ${invoiceData.vendor_name || 'Unknown'} $${invoiceData.total_amount || '?'}`,
    description: note
  };
  if (billId) taskArgs.documentId = billId;

  const body = {
    query: {
      $: { grantKey: JT_GRANT_KEY },
      createTask: {
        $: taskArgs,
        id: {},
        name: {}
      }
    }
  };

  const res = await httpsPost('api.jobtread.com', '/pave', { 'Content-Type': 'application/json' }, body);
  context.log(`createTask response: ${JSON.stringify(res).substring(0, 300)}`);
  return res;
}

module.exports = async function (context, myTimer) {
  context.log('ProcessVendorInvoice timer fired');

  try {
    const token = await getGraphToken();
    const emailsRes = await getUnprocessedEmails(token);
    const emails = emailsRes?.value || [];
    context.log(`Found ${emails.length} unprocessed emails`);

    if (emails.length === 0) return;

    const [vendors, jobs] = await Promise.all([
      getJTVendors(context),
      getActiveJobs(context)
    ]);

    context.log(`JT vendors: ${vendors.length}, jobs: ${jobs.length}, Kareem: ${KAREEM_ID}`);

    for (const email of emails) {
      try {
        if (!email.hasAttachments) {
          await markProcessed(token, email.id);
          continue;
        }

        const attachRes = await getAttachments(token, email.id);
        const pdfs = (attachRes?.value || []).filter(a =>
          a.contentType === 'application/pdf' || a.name?.toLowerCase().endsWith('.pdf')
        );

        if (pdfs.length === 0) {
          await markProcessed(token, email.id);
          continue;
        }

        for (const pdf of pdfs) {
          const invoiceData = await extractInvoiceData(pdf.contentBytes, pdf.name, context);
          if (!invoiceData) {
            context.log(`Failed to parse ${pdf.name}`);
            continue;
          }

          context.log(`Parsed: ${JSON.stringify(invoiceData)}`);

          if (!invoiceData.vendor_name && !invoiceData.total_amount) {
            context.log(`Not an invoice, skipping ${pdf.name}`);
            continue;
          }

          const matchedVendor = fuzzyMatch(invoiceData.vendor_name, vendors, 'name');
          const matchedJob =
            fuzzyMatch(invoiceData.job_name, jobs, 'name') ||
            fuzzyMatch(invoiceData.job_name, jobs, 'number') ||
            fuzzyMatch(invoiceData.job_name, jobs, 'address');

          context.log(`Vendor match: ${matchedVendor?.name || 'none'}, Job match: ${matchedJob?.name || 'none'}`);

          if (matchedJob?.id) {
            const bill = await createJTBill(
              matchedVendor?.id || null,
              matchedJob.id,
              invoiceData,
              context
            );
            await createJTTodo(bill?.id || null, invoiceData, matchedJob, matchedVendor, context);
            context.log(`Bill created: ${bill?.id}, Todo created`);
          } else {
            context.log(`No job match for "${invoiceData.job_name}" — todo only`);
            await createJTTodo(null, invoiceData, null, matchedVendor, context);
          }
        }

        await markProcessed(token, email.id);

      } catch (emailErr) {
        context.log(`Error processing email ${email.id}: ${emailErr.message}`);
      }
    }

  } catch (err) {
    context.log(`Fatal error: ${err.message}`);
  }
};